using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.MCP;

namespace SNChat.App.Services;

/// <summary>
/// Manages MCP (Model Context Protocol) server lifecycle and tool registration.
/// Spawns configured MCP servers, discovers their tools, and registers them
/// in the tool registry so the LLM can use them.
/// </summary>
public class McpService : IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly IToolRegistry _toolRegistry;
    private readonly SessionAccessGrants _grants;
    private readonly IAccessPrompt _accessPrompt;
    private readonly ILogger<McpService> _logger;
    private readonly List<McpServerConnection> _connections = new();
    private readonly List<(string ServerName, int ToolCount)> _serverInfo = new();

    public IReadOnlyList<(string ServerName, int ToolCount)> ConnectedServers => _serverInfo.AsReadOnly();

    public McpService(
        SettingsService settingsService,
        IToolRegistry toolRegistry,
        SessionAccessGrants grants,
        IAccessPrompt accessPrompt,
        ILogger<McpService> logger)
    {
        _settingsService = settingsService;
        _toolRegistry = toolRegistry;
        _grants = grants;
        _accessPrompt = accessPrompt;
        _logger = logger;
    }

    /// <summary>
    /// Initialize all enabled MCP servers and register their tools.
    /// Call this once during application startup.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetCachedSettings();
        var mcpServers = settings.Tools.McpServers.Where(s => s.Enabled).ToList();

        if (mcpServers.Count == 0)
        {
            _logger.LogInformation("No MCP servers configured");
            return;
        }

        _logger.LogInformation("Initializing {Count} MCP server(s)", mcpServers.Count);

        foreach (var serverConfig in mcpServers)
        {
            try
            {
                await InitializeServerAsync(serverConfig, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize MCP server {Name}", serverConfig.Name);
                // Continue with other servers even if one fails
            }
        }

        _logger.LogInformation("MCP initialization complete. {Count} server(s) connected, {ToolCount} tool(s) registered",
            _connections.Count, _serverInfo.Sum(s => s.ToolCount));
    }

    private async Task InitializeServerAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to MCP server: {Name} ({Command} {Args})",
            config.Name, config.Command, config.Arguments);

        // Wrapped in a connection rather than held as a client, so that granting
        // access to another folder can restart the server underneath the tools
        // already registered from it.
        var connection = await McpServerConnection.ConnectAsync(
            config.Command, config.Arguments, config.Env, cancellationToken);

        try
        {
            _logger.LogInformation("Connected to {ServerName} v{Version}",
                connection.Client.ServerInfo.Name, connection.Client.ServerInfo.Version);

            // Discover tools
            var tools = await connection.ListToolsAsync(cancellationToken);

            _logger.LogInformation("Discovered {Count} tool(s) from {Server}",
                tools.Count, config.Name);

            // Register each tool
            var registeredCount = 0;
            foreach (var mcpTool in tools)
            {
                try
                {
                    var adapter = new McpToolAdapter(
                        connection, mcpTool, _grants, _accessPrompt, _logger);

                    _toolRegistry.Register(adapter);
                    registeredCount++;

                    _logger.LogDebug("Registered MCP tool: {ToolName} from {Server}",
                        mcpTool.Name, config.Name);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to register tool {ToolName} from {Server}",
                        mcpTool.Name, config.Name);
                }
            }

            // Track this connection for cleanup
            _connections.Add(connection);
            _serverInfo.Add((config.Name, registeredCount));

            _logger.LogInformation("Successfully registered {Count}/{Total} tools from {Server}",
                registeredCount, tools.Count, config.Name);
        }
        catch
        {
            // Clean up the connection if discovery failed
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("Shutting down {Count} MCP server(s)", _connections.Count);

        foreach (var connection in _connections)
        {
            try
            {
                connection.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP connection");
            }
        }

        _connections.Clear();
        _serverInfo.Clear();
    }
}
