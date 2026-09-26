using SNChat.Core.Services;
using SNChat.MCP;

namespace SNChat.Tests;

/// <summary>
/// Checks the guard against the server it guards.
///
/// Everything <see cref="WindowsSystemGuard"/> decides rests on somebody else's
/// strings: a tool called service_manager, an action called stop_service. If the
/// server renames either, the guard stops recognising the dangerous call and
/// waves it straight through - no error, no dialog, no test failure anywhere
/// else. That is the failure this exists to catch.
///
/// Reading the documentation would not do. The integration guide these were
/// built from describes 42 tools with names like process_list and registry_write;
/// the server publishes 7, none of them called that, and has no way to write to
/// the registry at all. The names below came off the running server.
///
/// Skips itself when npx is unavailable rather than failing, in the manner of the
/// other toolchain-dependent tests here.
/// </summary>
public class WindowsSystemServerIntegrationTests
{
    /// <summary>Generous: the first run may download the package.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    private static async Task<McpServerConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        if (ToolchainLocatorShim.Find("npx.cmd") == null && ToolchainLocatorShim.Find("npx") == null)
            return null;

        try
        {
            return await McpServerConnection.ConnectAsync(
                "npx.cmd", "-y windows-system-mcp", cancellationToken: cancellationToken);
        }
        catch
        {
            // No network, or npx cannot fetch the package. Not a failure of
            // anything this repository owns.
            return null;
        }
    }

    [Fact]
    public async Task The_server_still_offers_the_tools_the_guard_watches()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var guarded in new[] { "filesystem", "process_manager", "service_manager", "network" })
        {
            Assert.True(
                names.Contains(guarded),
                $"The guard watches '{guarded}', which the server no longer has. " +
                $"It offers: {string.Join(", ", names)}");

            Assert.True(WindowsSystemGuard.Guards(guarded));
        }
    }

    [Fact]
    public async Task Every_action_the_guard_stops_is_one_the_server_can_actually_do()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);

        // A renamed action is the dangerous direction: the guard would not
        // recognise it, and the call would go through unasked.
        AssertHasActions(tools, "process_manager", "kill_process");
        AssertHasActions(tools, "service_manager", "start_service", "stop_service", "restart_service");
        AssertHasActions(tools, "network", "scan_open_ports");
        AssertHasActions(tools, "filesystem", "read_file");
    }

    [Fact]
    public async Task The_actions_left_unguarded_are_all_read_only()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);

        // A new action that changes something would otherwise arrive silently
        // in a future version and be offered to the model unguarded. Anything
        // whose name is not a plain read has to be looked at by a person.
        //
        // Matched on whole underscore-separated words rather than substrings,
        // because "get_startup_programs" is not "start" and flagging it would
        // teach whoever sees this failure to wave the next one through.
        var suspicious = new HashSet<string>(StringComparer.Ordinal)
        {
            "kill", "stop", "start", "restart", "write", "set", "delete", "remove",
            "create", "modify", "disable", "enable", "scan", "terminate"
        };

        var unguarded = new List<string>();
        var inspected = 0;

        foreach (var tool in tools)
        {
            foreach (var action in ActionsOf(tool))
            {
                inspected++;

                var args = new Dictionary<string, object?> { ["action"] = action };

                // scan_open_ports is judged on its host, so ask about a remote
                // one: locally it is a diagnostic and is deliberately allowed.
                if (tool.Name == "network")
                    args["host"] = "203.0.113.1";

                if (WindowsSystemGuard.NeedsConsent(tool.Name, args, out _))
                    continue;

                if (action.Split('_').Any(suspicious.Contains))
                    unguarded.Add($"{tool.Name}/{action}");
            }
        }

        // Without this the test passes by reading nothing at all: an action
        // enum that stopped deserializing would leave the loop empty and the
        // assertion below trivially true, which is precisely the silence this
        // whole class exists to break.
        Assert.True(
            inspected >= 50,
            $"Only {inspected} actions were inspected; the server publishes 56 across 7 " +
            "tools, so the schema is not being read properly and this test proves nothing.");

        Assert.True(
            unguarded.Count == 0,
            "These actions look like they change the machine but nothing asks the user " +
            $"about them: {string.Join(", ", unguarded)}");
    }

    [Fact]
    public async Task The_action_that_actually_went_wrong_is_redirected_to_the_right_tool()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);
        var actions = new WindowsSystemActions();

        foreach (var tool in tools.Where(t => WindowsSystemGuard.IsWindowsSystemTool(t.Name)))
            actions.Add(tool.Name, ActionsOf(tool).ToList());

        Assert.False(actions.IsEmpty, "No action enums were read, so the map is empty.");

        // Verbatim from a real run: the model wanted the CPU leaderboard and
        // asked process_manager, which does not have it.
        var explanation = actions.Explain(
            "process_manager",
            "❌ Process management operation failed: Unknown action: get_top_processes_by_cpu");

        Assert.NotNull(explanation);
        Assert.Contains("performance", explanation);
        Assert.Contains("get_top_processes", explanation);
    }

    private static IEnumerable<string> ActionsOf(SNChat.MCP.Protocol.Messages.McpTool tool) =>
        tool.InputSchema.Properties != null
        && tool.InputSchema.Properties.TryGetValue("action", out var action)
        && action.Enum != null
            ? action.Enum
            : Enumerable.Empty<string>();

    private static void AssertHasActions(
        List<SNChat.MCP.Protocol.Messages.McpTool> tools,
        string toolName,
        params string[] expected)
    {
        var tool = tools.FirstOrDefault(t => t.Name == toolName);

        Assert.True(tool != null, $"The server no longer has a '{toolName}' tool.");

        var actions = ActionsOf(tool!).ToHashSet(StringComparer.Ordinal);

        foreach (var action in expected)
        {
            Assert.True(
                actions.Contains(action),
                $"The guard keys on {toolName}/{action}, which the server no longer offers. " +
                $"It offers: {string.Join(", ", actions)}");
        }
    }
}
