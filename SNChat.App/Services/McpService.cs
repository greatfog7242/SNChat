using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.MCP;
using SNChat.MCP.Protocol.Messages;

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
    private readonly IActionPrompt _actionPrompt;
    private readonly ILogger<McpService> _logger;
    private readonly List<McpServerConnection> _connections = new();
    private readonly List<(string ServerName, int ToolCount)> _serverInfo = new();

    public IReadOnlyList<(string ServerName, int ToolCount)> ConnectedServers => _serverInfo.AsReadOnly();

    public McpService(
        SettingsService settingsService,
        IToolRegistry toolRegistry,
        SessionAccessGrants grants,
        IAccessPrompt accessPrompt,
        IActionPrompt actionPrompt,
        ILogger<McpService> logger)
    {
        _settingsService = settingsService;
        _toolRegistry = toolRegistry;
        _grants = grants;
        _accessPrompt = accessPrompt;
        _actionPrompt = actionPrompt;
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

        var windowsSystem = WindowsSystemServer(settings, mcpServers);

        if (windowsSystem != null)
            mcpServers.Add(windowsSystem);

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

    /// <summary>
    /// The windows-system-mcp server as a server config, when Settings asks for
    /// it and it is not already configured by hand.
    ///
    /// Launching it twice would be worse than useless: the second copy's tools
    /// all collide with the first's by name, so the registry would either reject
    /// them or shadow them, and either way a whole Windows server process would
    /// be running for nothing. Someone who wrote the entry themselves meant it,
    /// so theirs is the one that wins.
    /// </summary>
    private McpServerConfig? WindowsSystemServer(AppSettings settings, List<McpServerConfig> configured)
    {
        if (!settings.WindowsSystem.Enabled)
            return null;

        if (configured.Any(s => s.Arguments.Contains("windows-system-mcp", StringComparison.OrdinalIgnoreCase)))
        {
            _logger.LogInformation(
                "Windows system tools are on, but a windows-system-mcp server is already " +
                "configured by hand; using that one");

            return null;
        }

        return new McpServerConfig
        {
            Name = "Windows system",
            Command = settings.WindowsSystem.Command,
            Arguments = settings.WindowsSystem.Arguments,
            Enabled = true
        };
    }

    /// <summary>
    /// The values a tool's "action" argument accepts, or nothing when it has no
    /// such argument. Empty for every server but this one, which is what keeps
    /// the map to windows-system-mcp's tools.
    /// </summary>
    private static IReadOnlyList<string> ActionsOf(McpTool tool) =>
        tool.InputSchema.Properties != null
        && tool.InputSchema.Properties.TryGetValue("action", out var action)
        && action.Enum is { Count: > 0 }
            ? action.Enum
            : Array.Empty<string>();

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

            // Which windows-system-mcp action lives on which of its tools, taken
            // from what this server just said rather than a table written here,
            // so a renamed action cannot leave the two disagreeing. Built before
            // the loop because every tool's wrapper needs the whole map, not
            // just its own part of it.
            var actions = new WindowsSystemActions();

            foreach (var mcpTool in tools.Where(t => WindowsSystemGuard.IsWindowsSystemTool(t.Name)))
                actions.Add(mcpTool.Name, ActionsOf(mcpTool));

            // Register each tool
            var registeredCount = 0;
            foreach (var mcpTool in tools)
            {
                try
                {
                    ITool adapter = new McpToolAdapter(
                        connection, mcpTool, _grants, _accessPrompt, _logger);

                    // windows-system-mcp bundles "list the services" and "stop
                    // this service" into one tool, so what needs guarding is a
                    // call rather than a tool. Applied by tool name, which means
                    // a hand-written entry for the same server is guarded too.
                    //
                    // All seven are wrapped, not just the four that can stop to
                    // ask: the other three still misname actions, and that needs
                    // explaining wherever it happens.
                    if (WindowsSystemGuard.IsWindowsSystemTool(mcpTool.Name))
                    {
                        adapter = new GuardedWindowsSystemTool(
                            adapter, _settingsService, _grants, _accessPrompt, _actionPrompt,
                            actions, _logger);

                        _logger.LogDebug("Wrapped Windows system tool: {ToolName} (asks first: {Guards})",
                            mcpTool.Name, WindowsSystemGuard.Guards(mcpTool.Name));
                    }

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
