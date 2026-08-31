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
    private readonly ILogger<McpService> _logger;
    private readonly List<McpClient> _clients = new();
    private readonly List<(string ServerName, int ToolCount)> _serverInfo = new();

    public IReadOnlyList<(string ServerName, int ToolCount)> ConnectedServers => _serverInfo.AsReadOnly();

    public McpService(
        SettingsService settingsService,
        IToolRegistry toolRegistry,
        ILogger<McpService> logger)
    {
        _settingsService = settingsService;
        _toolRegistry = toolRegistry;
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
            _clients.Count, _serverInfo.Sum(s => s.ToolCount));
    }

    private async Task InitializeServerAsync(McpServerConfig config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connecting to MCP server: {Name} ({Command} {Args})",
            config.Name, config.Command, config.Arguments);

        // Create and initialize client
        var client = new McpClient(config.Command, config.Arguments, config.Env);
        client.ErrorReceived += (s, error) =>
        {
            _logger.LogWarning("MCP server {Name} error: {Error}", config.Name, error);
        };

        try
        {
            await client.InitializeAsync(cancellationToken);

            _logger.LogInformation("Connected to {ServerName} v{Version}",
                client.ServerInfo.Name, client.ServerInfo.Version);

            // Discover tools
            var tools = await client.ListToolsAsync(cancellationToken);

            _logger.LogInformation("Discovered {Count} tool(s) from {Server}",
                tools.Count, config.Name);

            // Register each tool
            var registeredCount = 0;
            foreach (var mcpTool in tools)
            {
                try
                {
                    var adapter = new McpToolAdapter(client, mcpTool);
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

            // Track this client for cleanup
            _clients.Add(client);
            _serverInfo.Add((config.Name, registeredCount));

            _logger.LogInformation("Successfully registered {Count}/{Total} tools from {Server}",
                registeredCount, tools.Count, config.Name);
        }
        catch
        {
            // Clean up client if initialization failed
            client.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _logger.LogInformation("Shutting down {Count} MCP server(s)", _clients.Count);

        foreach (var client in _clients)
        {
            try
            {
                client.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing MCP client");
            }
        }

        _clients.Clear();
        _serverInfo.Clear();
    }
}
