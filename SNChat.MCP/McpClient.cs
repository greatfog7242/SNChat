using System.Text.Json;
using SNChat.MCP.Protocol.JsonRpc;
using SNChat.MCP.Protocol.Messages;
using SNChat.MCP.Transport;

namespace SNChat.MCP;

/// <summary>
/// Client for communicating with MCP (Model Context Protocol) servers.
/// Handles connection lifecycle: initialize → ready → shutdown.
/// </summary>
public class McpClient : IDisposable
{
    private readonly StdioTransport _transport;
    private int _nextRequestId;
    private bool _initialized;

    public ServerCapabilities ServerCapabilities { get; private set; } = new();
    public Implementation ServerInfo { get; private set; } = new();

    public event EventHandler<string>? ErrorReceived;

    public McpClient(
        string serverCommand,
        string serverArguments = "",
        IReadOnlyDictionary<string, string>? environment = null)
    {
        _transport = new StdioTransport(serverCommand, serverArguments, environment);
        _transport.ErrorReceived += (s, e) => ErrorReceived?.Invoke(this, e);
    }

    /// <summary>
    /// Initialize the MCP connection. Must be called before any other operations.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var request = new JsonRpcRequest
        {
            Id = GetNextRequestId(),
            Method = "initialize",
            Params = new InitializeParams
            {
                ProtocolVersion = "2024-11-05",
                ClientInfo = new Implementation
                {
                    Name = "SNChat",
                    Version = "1.0.0"
                },
                Capabilities = new ClientCapabilities()
            }
        };

        var response = await _transport.SendRequestAsync(request, cancellationToken);

        if (response.Error != null)
            throw new McpException($"Initialize failed: {response.Error.Message}");

        var result = JsonSerializer.Deserialize<InitializeResult>(
            JsonSerializer.Serialize(response.Result));

        if (result == null)
            throw new McpException("Initialize response was null");

        ServerCapabilities = result.Capabilities;
        ServerInfo = result.ServerInfo;

        // Send initialized notification
        await _transport.SendNotificationAsync(new JsonRpcNotification
        {
            Method = "notifications/initialized"
        }, cancellationToken);

        _initialized = true;
    }

    /// <summary>
    /// List all tools available from the MCP server.
    /// </summary>
    public async Task<List<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var request = new JsonRpcRequest
        {
            Id = GetNextRequestId(),
            Method = "tools/list",
            Params = new { }
        };

        var response = await _transport.SendRequestAsync(request, cancellationToken);

        if (response.Error != null)
            throw new McpException($"ListTools failed: {response.Error.Message}");

        var result = JsonSerializer.Deserialize<ListToolsResult>(
            JsonSerializer.Serialize(response.Result));

        return result?.Tools ?? new List<McpTool>();
    }

    /// <summary>
    /// Call a tool on the MCP server.
    /// </summary>
    public async Task<CallToolResult> CallToolAsync(
        string toolName,
        Dictionary<string, object>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var request = new JsonRpcRequest
        {
            Id = GetNextRequestId(),
            Method = "tools/call",
            Params = new CallToolParams
            {
                Name = toolName,
                Arguments = arguments
            }
        };

        var response = await _transport.SendRequestAsync(request, cancellationToken);

        if (response.Error != null)
            throw new McpException($"CallTool failed: {response.Error.Message}");

        var result = JsonSerializer.Deserialize<CallToolResult>(
            JsonSerializer.Serialize(response.Result));

        return result ?? new CallToolResult();
    }

    /// <summary>
    /// List all resources available from the MCP server.
    /// </summary>
    public async Task<List<Resource>> ListResourcesAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var request = new JsonRpcRequest
        {
            Id = GetNextRequestId(),
            Method = "resources/list",
            Params = new { }
        };

        var response = await _transport.SendRequestAsync(request, cancellationToken);

        if (response.Error != null)
            throw new McpException($"ListResources failed: {response.Error.Message}");

        var result = JsonSerializer.Deserialize<ListResourcesResult>(
            JsonSerializer.Serialize(response.Result));

        return result?.Resources ?? new List<Resource>();
    }

    /// <summary>
    /// List all prompts available from the MCP server.
    /// </summary>
    public async Task<List<Prompt>> ListPromptsAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();

        var request = new JsonRpcRequest
        {
            Id = GetNextRequestId(),
            Method = "prompts/list",
            Params = new { }
        };

        var response = await _transport.SendRequestAsync(request, cancellationToken);

        if (response.Error != null)
            throw new McpException($"ListPrompts failed: {response.Error.Message}");

        var result = JsonSerializer.Deserialize<ListPromptsResult>(
            JsonSerializer.Serialize(response.Result));

        return result?.Prompts ?? new List<Prompt>();
    }

    private int GetNextRequestId() => Interlocked.Increment(ref _nextRequestId);

    private void EnsureInitialized()
    {
        if (!_initialized)
            throw new InvalidOperationException("Client must be initialized before calling this method");
    }

    public void Dispose()
    {
        _transport.Dispose();
    }
}

public class McpException : Exception
{
    public McpException(string message) : base(message) { }
    public McpException(string message, Exception innerException) : base(message, innerException) { }
}
