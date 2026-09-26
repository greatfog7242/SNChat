using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Which windows-system-mcp calls are let through, and which have to be agreed
/// to first.
///
/// The distinction lives in an argument rather than in a tool name - the server
/// bundles "list the services" and "stop this service" into one tool called
/// service_manager - so getting it wrong in either direction is quiet. Too
/// strict and every question about the machine puts a dialog in the way; too
/// loose and a process dies without anyone being asked.
/// </summary>
public class WindowsSystemGuardTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    [Theory]
    [InlineData("process_manager", "list_processes")]
    [InlineData("process_manager", "get_process_details")]
    [InlineData("process_manager", "get_top_processes")]
    [InlineData("service_manager", "list_services")]
    [InlineData("service_manager", "get_service_status")]
    [InlineData("service_manager", "get_running_services")]
    [InlineData("filesystem", "list_directory")]
    [InlineData("filesystem", "get_disk_usage")]
    [InlineData("network", "get_active_connections")]
    [InlineData("network", "ping_host")]
    public void Reading_the_machine_is_not_asked_about(string tool, string action)
    {
        Assert.False(
            WindowsSystemGuard.NeedsConsent(tool, Args(("action", action)), out _),
            $"{tool}/{action} only reports, so a dialog here is friction for nothing.");
    }

    [Fact]
    public void Killing_a_process_needs_the_process_switch_and_names_its_target()
    {
        Assert.True(WindowsSystemGuard.NeedsConsent(
            "process_manager",
            Args(("action", "kill_process"), ("process_id", 4821)),
            out var confirmable));

        Assert.Equal(WindowsSystemGuard.Capability.ProcessControl, confirmable.Capability);

        // The pid is what makes the question answerable.
        Assert.Contains("4821", confirmable.Detail);
    }

    [Fact]
    public void A_process_named_rather_than_numbered_is_still_named_in_the_dialog()
    {
        Assert.True(WindowsSystemGuard.NeedsConsent(
            "process_manager",
            Args(("action", "kill_process"), ("process_name", "notepad.exe")),
            out var confirmable));

        Assert.Contains("notepad.exe", confirmable.Detail);
        Assert.DoesNotContain("not specified", confirmable.Detail);
    }

    [Theory]
    [InlineData("stop_service")]
    [InlineData("start_service")]
    [InlineData("restart_service")]
    public void Moving_a_service_needs_the_service_switch(string action)
    {
        Assert.True(WindowsSystemGuard.NeedsConsent(
            "service_manager",
            Args(("action", action), ("service_name", "MSSQLSERVER")),
            out var confirmable));

        Assert.Equal(WindowsSystemGuard.Capability.ServiceControl, confirmable.Capability);
        Assert.Contains("MSSQLSERVER", confirmable.Detail);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("")]
    public void Scanning_this_machine_is_a_diagnostic_and_is_left_alone(string host)
    {
        Assert.False(WindowsSystemGuard.NeedsConsent(
            "network",
            Args(("action", "scan_open_ports"), ("host", host)),
            out _));
    }

    [Fact]
    public void Scanning_somebody_elses_machine_is_asked_about_with_no_switch_to_turn_on()
    {
        Assert.True(WindowsSystemGuard.NeedsConsent(
            "network",
            Args(("action", "scan_open_ports"), ("host", "192.168.1.50")),
            out var confirmable));

        // No setting governs this one: it is rare, and outward-facing enough
        // that a standing permission would be the wrong shape of answer.
        Assert.Equal(WindowsSystemGuard.Capability.None, confirmable.Capability);
        Assert.Contains("192.168.1.50", confirmable.Detail);

        Assert.True(WindowsSystemGuard.IsPermitted(
            confirmable.Capability, new WindowsSystemSettings(), out _));
    }

    [Fact]
    public void A_call_with_no_action_is_not_mistaken_for_a_dangerous_one()
    {
        // The server would reject this itself; the guard must not read the
        // missing argument as some default that happens to match.
        Assert.False(WindowsSystemGuard.NeedsConsent("service_manager", Args(), out _));
        Assert.False(WindowsSystemGuard.NeedsConsent("process_manager", Args(("action", "")), out _));
    }

    [Fact]
    public void The_switches_are_off_until_turned_on_and_the_refusal_says_where()
    {
        var settings = new WindowsSystemSettings();

        Assert.False(settings.AllowProcessControl);
        Assert.False(settings.AllowServiceControl);
        Assert.False(settings.Enabled);

        Assert.False(WindowsSystemGuard.IsPermitted(
            WindowsSystemGuard.Capability.ProcessControl, settings, out var processRefusal));

        Assert.False(WindowsSystemGuard.IsPermitted(
            WindowsSystemGuard.Capability.ServiceControl, settings, out var serviceRefusal));

        // A model told only "no" tries again in a different shape.
        Assert.Contains("Settings", processRefusal);
        Assert.Contains("Settings", serviceRefusal);
    }

    [Fact]
    public void Turning_a_switch_on_permits_only_that_one()
    {
        var settings = new WindowsSystemSettings { AllowProcessControl = true };

        Assert.True(WindowsSystemGuard.IsPermitted(
            WindowsSystemGuard.Capability.ProcessControl, settings, out _));

        Assert.False(WindowsSystemGuard.IsPermitted(
            WindowsSystemGuard.Capability.ServiceControl, settings, out _));
    }

    [Fact]
    public void Reading_a_files_contents_goes_through_folder_consent()
    {
        var path = WindowsSystemGuard.FileToConsentTo(
            "filesystem",
            Args(("action", "read_file"), ("path", @"D:\work\notes.txt")));

        Assert.Equal(@"D:\work\notes.txt", path);
    }

    [Theory]
    [InlineData("list_directory")]
    [InlineData("get_file_info")]
    [InlineData("find_large_files")]
    [InlineData("search_files")]
    [InlineData("get_disk_usage")]
    public void Names_and_sizes_do_not(string action)
    {
        // What makes the tool worth having is answering "what is filling my
        // disk", which is all names and sizes. Contents are the line.
        Assert.Null(WindowsSystemGuard.FileToConsentTo(
            "filesystem", Args(("action", action), ("path", @"D:\work"))));
    }

    [Fact]
    public void Only_four_of_the_seven_tools_can_ever_stop_to_ask()
    {
        Assert.True(WindowsSystemGuard.Guards("filesystem"));
        Assert.True(WindowsSystemGuard.Guards("process_manager"));
        Assert.True(WindowsSystemGuard.Guards("service_manager"));
        Assert.True(WindowsSystemGuard.Guards("network"));

        // Report-only: nothing they do needs agreeing to.
        Assert.False(WindowsSystemGuard.Guards("system_info"));
        Assert.False(WindowsSystemGuard.Guards("registry"));
        Assert.False(WindowsSystemGuard.Guards("performance"));

        // And nothing from any other MCP server.
        Assert.False(WindowsSystemGuard.Guards("read_text_file"));
        Assert.False(WindowsSystemGuard.Guards("web_search"));
    }

    [Fact]
    public void All_seven_are_still_wrapped_so_a_misnamed_action_gets_explained()
    {
        // Wider than Guards on purpose: the three report-only tools cannot ask
        // the user anything, but they can still be called with an action that
        // belongs to one of their siblings.
        foreach (var tool in new[]
                 {
                     "filesystem", "process_manager", "system_info", "registry",
                     "service_manager", "network", "performance"
                 })
        {
            Assert.True(WindowsSystemGuard.IsWindowsSystemTool(tool));
        }

        Assert.Equal(7, WindowsSystemGuard.ToolNames.Count);

        Assert.False(WindowsSystemGuard.IsWindowsSystemTool("read_text_file"));
        Assert.False(WindowsSystemGuard.IsWindowsSystemTool("web_search"));
    }

    [Fact]
    public void Arguments_that_arrive_as_numbers_or_odd_types_still_match()
    {
        // Arguments come from JSON, so an action is a string but a pid may be a
        // long, an int, or a JsonElement depending on the provider.
        Assert.True(WindowsSystemGuard.NeedsConsent(
            "process_manager",
            Args(("action", "kill_process"), ("process_id", 4821L)),
            out var fromLong));

        Assert.Contains("4821", fromLong.Detail);

        Assert.True(WindowsSystemGuard.NeedsConsent(
            "process_manager",
            Args(("action", " kill_process "), ("process_id", "9")),
            out _));
    }
}
