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

    private readonly Dictionary<object, TaskCompletionSource<JsonRpcResponse>> _pendingRequests = new();
    private readonly object _lock = new();

    public event EventHandler<JsonRpcNotification>? NotificationReceived;
    public event EventHandler<string>? ErrorReceived;

    public StdioTransport(string command, string arguments = "")
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardInputEncoding = Encoding.UTF8
        };

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

    /// <summary>Send a JSON-RPC request and wait for the response.</summary>
    public async Task<JsonRpcResponse> SendRequestAsync(JsonRpcRequest request, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<JsonRpcResponse>();

        lock (_lock)
        {
            _pendingRequests[request.Id!] = tcs;
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
                _pendingRequests.Remove(request.Id!);
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
                            lock (_lock)
                            {
                                _pendingRequests.TryGetValue(response.Id, out tcs);
                                _pendingRequests.Remove(response.Id);
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
