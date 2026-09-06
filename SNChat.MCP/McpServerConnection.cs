using SNChat.MCP.Protocol.Messages;

namespace SNChat.MCP;

/// <summary>
/// One MCP server, and the ability to restart it with more folders allowed.
///
/// The indirection exists so that restarting does not invalidate anything.
/// Callers hold this rather than the client, so swapping the client underneath
/// them is invisible: tools registered from this server keep working, the model
/// keeps the same tool list, and only the process behind them is new.
///
/// Restarting is the mechanism because it is the only one that works. The
/// filesystem server advertises the MCP "roots" capability and does ask the
/// client for its roots - but as of @modelcontextprotocol/server-filesystem
/// 0.2.0 it ignores the answer, both with and without directories on its command
/// line, and its allowed list stays exactly as it was launched. That was
/// established by driving the real server, not read from its documentation.
/// </summary>
public sealed class McpServerConnection : IDisposable
{
    private readonly string _command;
    private readonly string _baseArguments;
    private readonly Dictionary<string, string> _environment;
    private readonly SemaphoreSlim _restartGate = new(1, 1);
    private readonly List<string> _extraDirectories = new();

    private McpClient _client;
    private bool _disposed;

    /// <summary>The client to talk to right now. Changes when the server restarts.</summary>
    public McpClient Client => _client;

    /// <summary>Folders added since launch, in the order they were granted.</summary>
    public IReadOnlyList<string> AddedDirectories
    {
        get { lock (_extraDirectories) return _extraDirectories.ToList(); }
    }

    private McpServerConnection(
        string command,
        string baseArguments,
        Dictionary<string, string> environment,
        McpClient client)
    {
        _command = command;
        _baseArguments = baseArguments;
        _environment = environment;
        _client = client;
    }

    public static async Task<McpServerConnection> ConnectAsync(
        string command,
        string arguments,
        Dictionary<string, string>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var env = environment ?? new Dictionary<string, string>();
        var client = new McpClient(command, arguments, env);

        try
        {
            await client.InitializeAsync(cancellationToken);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new McpServerConnection(command, arguments, env, client);
    }

    public Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default) =>
        _client.ListToolsAsync(cancellationToken);

    public Task<CallToolResult> CallToolAsync(
        string name,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(name, arguments, cancellationToken);

    /// <summary>
    /// Whether this server takes folder arguments, and so could conceivably be
    /// widened. Only a filesystem server is; restarting a search server with a
    /// folder appended to its command line would simply break it.
    /// </summary>
    public bool CanWiden =>
        _baseArguments.Contains("server-filesystem", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Restarts the server with <paramref name="folder"/> added to the folders it
    /// may reach, and reports whether that worked.
    ///
    /// On failure the previous server is left running and usable, so a grant that
    /// cannot be taken up costs the grant rather than the whole server.
    /// </summary>
    public async Task<bool> AllowFolderAsync(string folder, CancellationToken cancellationToken = default)
    {
        await _restartGate.WaitAsync(cancellationToken);

        try
        {
            if (_disposed)
                return false;

            var full = Normalise(folder);

            lock (_extraDirectories)
            {
                if (_extraDirectories.Any(d => string.Equals(d, full, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }

            List<string> wanted;

            lock (_extraDirectories)
                wanted = _extraDirectories.Append(full).ToList();

            var replacement = new McpClient(_command, BuildArguments(wanted), _environment);

            try
            {
                await replacement.InitializeAsync(cancellationToken);

                // Proof rather than assumption. A server that started but did not
                // take the folder would otherwise look like a successful grant,
                // and the next call would fail identically with no explanation.
                if (!await CanReachAsync(replacement, full, cancellationToken))
                {
                    replacement.Dispose();
                    return false;
                }
            }
            catch
            {
                replacement.Dispose();
                return false;
            }

            var previous = _client;
            _client = replacement;

            lock (_extraDirectories)
                _extraDirectories.Add(full);

            try
            {
                previous.Dispose();
            }
            catch
            {
                // The replacement is already serving; a stubborn old process is
                // not worth failing the grant over.
            }

            return true;
        }
        finally
        {
            _restartGate.Release();
        }
    }

    /// <summary>
    /// Asks the freshly started server what it is allowed to reach, and checks
    /// the new folder is in the list.
    /// </summary>
    private static async Task<bool> CanReachAsync(
        McpClient client,
        string folder,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await client.CallToolAsync(
                "list_allowed_directories", new Dictionary<string, object>(), cancellationToken);

            var text = string.Join("\n", result.Content.Select(c => c.Text ?? string.Empty));

            return text.Contains(folder, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // An older server without that tool: fall back to trusting the
            // command line, which is what was asked for either way.
            return true;
        }
    }

    private string BuildArguments(IReadOnlyList<string> extraDirectories)
    {
        var arguments = _baseArguments;

        foreach (var directory in extraDirectories)
            arguments += " " + Quote(directory);

        return arguments;
    }

    private static string Normalise(string folder)
    {
        var full = Path.GetFullPath(folder);
        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    /// <summary>
    /// Wraps a path for a Windows command line.
    ///
    /// Normalise has already removed any trailing separator, which matters more
    /// than it looks: "C:\folder\" quoted becomes "C:\folder\", where the
    /// backslash escapes the closing quote and the argument swallows whatever
    /// follows. Harmless with one folder, wrong with two.
    /// </summary>
    public static string Quote(string path) => "\"" + path + "\"";

    public void Dispose()
    {
        _disposed = true;

        try
        {
            _client.Dispose();
        }
        catch
        {
            // Shutting down; a server that will not close cleanly is not worth
            // failing over.
        }

        _restartGate.Dispose();
    }
}
