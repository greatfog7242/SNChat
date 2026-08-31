using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SNChat.MCP.Protocol.JsonRpc;

namespace SNChat.MCP.Transport;

/// <summary>
/// Transports JSON-RPC messages over stdio to a child process.
/// The MCP server runs as a subprocess and communicates via stdin/stdout.
/// </summary>
public class StdioTransport : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly Task _readTask;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// Keyed by the request id rendered as text rather than by the id object
    /// itself. Outgoing ids are boxed ints, but an incoming id deserializes into
    /// object? as a JsonElement, and a JsonElement never compares equal to a
    /// boxed int. Keying on the objects would mean every response is read,
    /// matched against nothing, and discarded, leaving the caller waiting until
    /// it times out.
    /// </summary>
    private readonly Dictionary<string, TaskCompletionSource<JsonRpcResponse>> _pendingRequests = new();
    private readonly object _lock = new();

    /// <summary>
    /// Renders a JSON-RPC id as a stable key. The spec allows numbers or
    /// strings, and a server may echo the id back in either form.
    /// </summary>
    private static string IdKey(object? id) => id switch
    {
        null => string.Empty,
        JsonElement e when e.ValueKind == JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonElement e => e.GetRawText(),
        _ => id.ToString() ?? string.Empty
    };

    /// <summary>
    /// UTF-8 without a byte order mark. Encoding.UTF8 emits a BOM, which would
    /// prefix the first line we write with U+FEFF and make it invalid JSON. The
    /// server discards that line and never answers the initialize request, so
    /// the handshake hangs until it times out with nothing on stderr to explain
    /// why.
    /// </summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public event EventHandler<JsonRpcNotification>? NotificationReceived;
    public event EventHandler<string>? ErrorReceived;

    public StdioTransport(
        string command,
        string arguments = "",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ResolveCommand(command),
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardInputEncoding = Utf8NoBom
        };

        // Added on top of the inherited environment rather than replacing it, so
        // a server still finds PATH and the rest of what it needs to run.
        if (environment != null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        _process = new Process { StartInfo = startInfo };

        _process.ErrorDataReceived += (sender, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                ErrorReceived?.Invoke(this, e.Data);
        };

        _process.Start();
        _process.BeginErrorReadLine();

        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;

        // Start reading messages from stdout in background
        _readTask = Task.Run(ReadMessagesAsync, _cts.Token);
    }

    /// <summary>
    /// Finds the executable a bare command name refers to.
    ///
    /// Starting a process with UseShellExecute disabled goes straight to
    /// CreateProcess, which does not consult PATHEXT. The MCP servers people
    /// actually configure are reached through batch shims on Windows - npx is
    /// npx.cmd, uvx is uvx.exe - so a config saying "npx" fails with "the system
    /// cannot find the file specified" unless the extension is filled in here.
    /// Requiring "npx.cmd" in the config instead would be a platform detail
    /// leaking into every user's settings file, and would not match the command
    /// every MCP server's own README tells them to use.
    ///
    /// Returns the command unchanged when it cannot be resolved, so the failure
    /// still surfaces as the normal Win32Exception rather than something opaque.
    /// </summary>
    private static string ResolveCommand(string command)
    {
        if (!OperatingSystem.IsWindows())
            return command;

        // An explicit path or extension is already unambiguous.
        if (Path.IsPathRooted(command) || Path.HasExtension(command))
            return command;

        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        var searchPaths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in searchPaths)
        {
            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(directory.Trim(), command + extension);
                }
                catch (ArgumentException)
                {
                    // PATH entries with invalid characters are not worth failing over.
                    break;
                }

                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return command;
    }

    /// <summary>Send a JSON-RPC request and wait for the response.</summary>
    public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<JsonRpcResponse>();

        lock (_lock)
        {
            _pendingRequests[IdKey(request.Id)] = tcs;
        }

        try
        {
            await SendMessageAsync(request, cancellationToken);

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            linkedCts.Token.Register(() => tcs.TrySetCanceled());

            return await tcs.Task;
        }
        catch
        {
            lock (_lock)
            {
                _pendingRequests.Remove(IdKey(request.Id));
            }
            throw;
        }
    }

    /// <summary>Send a JSON-RPC notification (no response expected).</summary>
    public async Task SendNotificationAsync(JsonRpcNotification notification, CancellationToken cancellationToken = default)
    {
        await SendMessageAsync(notification, cancellationToken);
    }

    private async Task SendMessageAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, new JsonSerializerOptions
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        });

        await _stdin.WriteLineAsync(json);
        await _stdin.FlushAsync();
    }

    private async Task ReadMessagesAsync()
    {
        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var line = await _stdout.ReadLineAsync(_cts.Token);
                if (line == null)
                    break; // EOF

                try
                {
                    // Try to determine message type
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    // Check if it's a response (has "result" or "error")
                    if (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _))
                    {
                        var response = JsonSerializer.Deserialize<JsonRpcResponse>(line);
                        if (response?.Id != null)
                        {
                            TaskCompletionSource<JsonRpcResponse>? tcs;
                            var key = IdKey(response.Id);
                            lock (_lock)
                            {
                                _pendingRequests.TryGetValue(key, out tcs);
                                _pendingRequests.Remove(key);
                            }
                            tcs?.TrySetResult(response);
                        }
                    }
                    // Check if it's a notification (has "method" but no "id")
                    else if (root.TryGetProperty("method", out _) && !root.TryGetProperty("id", out _))
                    {
                        var notification = JsonSerializer.Deserialize<JsonRpcNotification>(line);
                        if (notification != null)
                            NotificationReceived?.Invoke(this, notification);
                    }
                }
                catch (JsonException ex)
                {
                    ErrorReceived?.Invoke(this, $"Failed to parse JSON-RPC message: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            ErrorReceived?.Invoke(this, $"Error reading messages: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _stdin.Close();
            _stdout.Close();

            if (!_process.WaitForExit(5000))
                _process.Kill();

            _process.Dispose();
        }
        catch
        {
            // Best effort cleanup
        }

        _cts.Dispose();
    }
}
