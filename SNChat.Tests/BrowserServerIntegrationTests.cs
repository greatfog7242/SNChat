using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.MCP;

namespace SNChat.Tests;

/// <summary>
/// Checks the browser guard against the chrome-devtools-mcp server it guards.
///
/// <see cref="BrowserGuard"/> decides which tools to allow based on tool names
/// from chrome-devtools-mcp. If the server renames a tool or adds a new
/// high-risk one, the guard could either fail to protect or unnecessarily block.
/// This test catches both cases.
///
/// Tool names came from chrome-devtools-mcp v1.10.1 on 2026-09-25.
/// See SQLITE_AND_BROWSER_MCP_PLAN.md.
///
/// Skips itself when npx/Node.js is unavailable or Chrome cannot be launched.
/// </summary>
public class BrowserServerIntegrationTests
{
    /// <summary>Generous: first run may download package and Chrome.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(5);

    private static async Task<McpServerConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        if (ToolchainLocatorShim.Find("npx.cmd") == null && ToolchainLocatorShim.Find("npx") == null)
            return null;

        try
        {
            // Use isolated and headless to avoid UI and keep it safe for CI
            return await McpServerConnection.ConnectAsync(
                "npx.cmd",
                "-y chrome-devtools-mcp@latest --isolated --headless --no-usage-statistics --no-performance-crux",
                cancellationToken: cancellationToken);
        }
        catch
        {
            // No network, Chrome not installed, or npx cannot fetch the package.
            // Not a failure of anything this repository owns.
            return null;
        }
    }

    [Fact]
    public async Task The_server_still_offers_the_observational_tools_we_allow()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        // Check a sample of observational tools we explicitly allow
        var expectedObservational = new[]
        {
            "list_pages", "take_screenshot", "list_console_messages",
            "list_network_requests", "lighthouse_audit"
        };

        foreach (var expected in expectedObservational)
        {
            Assert.True(
                names.Contains(expected),
                $"Expected observational tool '{expected}' is no longer offered by the server. " +
                $"Available tools: {string.Join(", ", names)}");

            Assert.True(
                BrowserGuard.IsBrowserTool(expected),
                $"'{expected}' should be recognized as an allowed browser tool");
        }
    }

    [Fact]
    public async Task The_server_still_offers_the_navigation_tools_we_allow()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);
        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var navTool in BrowserGuard.NavigationTools)
        {
            Assert.True(
                names.Contains(navTool),
                $"Navigation tool '{navTool}' is no longer offered by the server. " +
                $"Available tools: {string.Join(", ", names)}");

            Assert.True(
                BrowserGuard.NeedsDomainCheck(navTool),
                $"'{navTool}' should require domain checking");
        }
    }

    [Fact]
    public async Task Excluded_tools_are_not_accidentally_in_the_allowed_list()
    {
        // The dangerous case: if a high-risk tool appears in AllowedToolNames,
        // it would be registered and usable without proper guards.

        var overlap = BrowserGuard.AllowedToolNames
            .Intersect(BrowserGuard.HighRiskTools)
            .ToList();

        Assert.True(
            overlap.Count == 0,
            $"High-risk tools found in allowed list: {string.Join(", ", overlap)}");

        var interactionOverlap = BrowserGuard.AllowedToolNames
            .Intersect(BrowserGuard.InteractionTools)
            .ToList();

        Assert.True(
            interactionOverlap.Count == 0,
            $"Interaction tools found in allowed list: {string.Join(", ", interactionOverlap)}");
    }

    [Fact]
    public async Task New_tools_from_the_server_are_either_allowed_or_explicitly_excluded()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);
        var serverToolNames = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);

        // Every tool the server offers should be in one of our lists
        var knownTools = BrowserGuard.AllowedToolNames
            .Concat(BrowserGuard.InteractionTools)
            .Concat(BrowserGuard.HighRiskTools)
            .ToHashSet(StringComparer.Ordinal);

        var unknownTools = serverToolNames.Except(knownTools).ToList();

        Assert.True(
            unknownTools.Count == 0,
            $"The server offers tools not in any guard list. A person must classify these: " +
            $"{string.Join(", ", unknownTools)}. Either add them to AllowedToolNames or to " +
            "InteractionTools/HighRiskTools to explicitly exclude them.");
    }

    [Fact]
    public async Task File_writing_tools_are_correctly_identified()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);

        foreach (var (toolName, paramNames) in BrowserGuard.FileWritingTools)
        {
            Assert.True(
                BrowserGuard.IsBrowserTool(toolName),
                $"File-writing tool '{toolName}' should be in the allowed tools list");

            var tool = tools.FirstOrDefault(t => t.Name == toolName);

            Assert.True(tool != null, $"'{toolName}' is no longer offered by the server");

            // The argument names are the whole guard. If the server renames
            // filePath, the path is never inspected and the check silently stops
            // happening - no error, no test failure anywhere else.
            var props = tool!.InputSchema.Properties?.Keys.ToHashSet(StringComparer.Ordinal)
                        ?? new HashSet<string>(StringComparer.Ordinal);

            foreach (var paramName in paramNames)
            {
                Assert.True(
                    props.Contains(paramName),
                    $"The guard reads '{toolName}.{paramName}', which the server no longer has. " +
                    $"It takes: {string.Join(", ", props)}");
            }
        }
    }

    [Fact]
    public async Task Every_path_argument_the_server_takes_is_one_the_guard_reads()
    {
        using var cts = new CancellationTokenSource(Patience);
        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var tools = await connection.ListToolsAsync(cts.Token);
        var missed = new List<string>();

        // The dangerous direction: a registered tool grows a new path argument
        // in some later version and writes wherever it is told, unchecked.
        foreach (var tool in tools.Where(t => BrowserGuard.IsBrowserTool(t.Name)))
        {
            var known = BrowserGuard.FileWritingTools.TryGetValue(tool.Name, out var p)
                ? p
                : Array.Empty<string>();

            var pathArgs = (tool.InputSchema.Properties?.Keys ?? Enumerable.Empty<string>())
                .Where(name => name.Contains("path", StringComparison.OrdinalIgnoreCase));

            foreach (var pathArg in pathArgs.Where(a => !known.Contains(a, StringComparer.Ordinal)))
                missed.Add($"{tool.Name}.{pathArg}");
        }

        Assert.True(
            missed.Count == 0,
            "These registered tools take a path argument the guard does not inspect, so they " +
            $"would write wherever they are told: {string.Join(", ", missed)}");
    }

    [Fact]
    public void Closing_a_tab_is_not_treated_as_navigation()
    {
        // close_page takes no URL. Domain-checking it refused every call.
        Assert.False(BrowserGuard.NeedsDomainCheck("close_page"));
        Assert.True(BrowserGuard.IsBrowserTool("close_page"));
    }

    [Fact]
    public void Going_back_is_allowed_without_a_url()
    {
        // navigate_page does back, forward and reload with no "url" argument.
        // Treating a missing URL as a refusal broke all three.
        var back = new Dictionary<string, object?> { ["pageId"] = 1, ["type"] = "back" };

        Assert.Null(BrowserGuard.GetUrlArgument("navigate_page", back));

        var blank = new Dictionary<string, object?> { ["pageId"] = 1, ["url"] = "  " };

        Assert.Null(BrowserGuard.GetUrlArgument("navigate_page", blank));
    }

    [Fact]
    public void A_relative_path_is_refused_with_a_reason()
    {
        // The server resolves a bare name against its own working directory,
        // so this can never be what the model meant.
        Assert.False(
            BrowserGuard.IsWritablePath("shot.png", new[] { @"C:\work" }, out var refusal));

        Assert.Contains("relative", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\work", refusal);
    }

    [Fact]
    public void A_path_outside_the_workspace_roots_names_the_roots()
    {
        Assert.False(
            BrowserGuard.IsWritablePath(@"C:\elsewhere\shot.png", new[] { @"C:\work" }, out var refusal));

        // The server's own refusal names no alternative, which leaves the model
        // guessing at paths until it runs out of turns.
        Assert.Contains(@"C:\work", refusal);

        Assert.True(
            BrowserGuard.IsWritablePath(@"C:\work\sub\shot.png", new[] { @"C:\work" }, out _));

        // The separator check: C:\workshop is not inside C:\work.
        Assert.False(
            BrowserGuard.IsWritablePath(@"C:\workshop\shot.png", new[] { @"C:\work" }, out _));
    }

    [Fact]
    public void With_no_roots_configured_only_the_temp_directory_is_writable()
    {
        var roots = Array.Empty<string>();

        Assert.True(
            BrowserGuard.IsWritablePath(
                Path.Combine(Path.GetTempPath(), "shot.png"), roots, out _));

        Assert.False(
            BrowserGuard.IsWritablePath(@"C:\ai-playground\shot.png", roots, out var refusal));

        Assert.Contains("WorkspaceRoots", refusal);
    }

    [Fact]
    public void Domain_allowlist_validation_works_correctly()
    {
        // Empty allowlist allows everything
        Assert.True(
            BrowserGuard.IsUrlAllowed("https://example.com", Array.Empty<string>(), out var domain1));
        Assert.Equal("example.com", domain1);

        // Exact domain match
        Assert.True(
            BrowserGuard.IsUrlAllowed("https://github.com/path", new[] { "github.com" }, out var domain2));
        Assert.Equal("github.com", domain2);

        // Subdomain match
        Assert.True(
            BrowserGuard.IsUrlAllowed("https://api.github.com/users", new[] { "github.com" }, out var domain3));
        Assert.Equal("api.github.com", domain3);

        // Not in allowlist
        Assert.False(
            BrowserGuard.IsUrlAllowed("https://evil.com", new[] { "github.com", "google.com" }, out _));

        // Invalid URL
        Assert.False(
            BrowserGuard.IsUrlAllowed("not a url", new[] { "github.com" }, out _));
    }

    [Fact]
    public void Settings_validation_catches_unsafe_configurations()
    {
        // Valid: isolated profile, empty allowlist is fine
        var settings1 = new AppSettings().Browser;
        settings1.Enabled = true;
        settings1.UseIsolatedProfile = true;
        Assert.Null(BrowserGuard.ValidateSettings(settings1));

        // Valid: isolated profile, with allowlist is also fine
        var settings2 = new AppSettings().Browser;
        settings2.Enabled = true;
        settings2.UseIsolatedProfile = true;
        settings2.AllowedDomains = new List<string> { "example.com" };
        Assert.Null(BrowserGuard.ValidateSettings(settings2));

        // Valid: non-isolated with allowlist is permitted
        var settings3 = new AppSettings().Browser;
        settings3.Enabled = true;
        settings3.UseIsolatedProfile = false;
        settings3.AllowedDomains = new List<string> { "example.com" };
        Assert.Null(BrowserGuard.ValidateSettings(settings3));

        // Invalid: non-isolated without allowlist is dangerous
        var settings4 = new AppSettings().Browser;
        settings4.Enabled = true;
        settings4.UseIsolatedProfile = false;
        settings4.AllowedDomains = new List<string>();
        var error = BrowserGuard.ValidateSettings(settings4);
        Assert.NotNull(error);
        Assert.Contains("AllowedDomains must contain at least one domain", error);
    }
}
