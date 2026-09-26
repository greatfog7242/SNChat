using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Recovering from windows-system-mcp's "Unknown action" refusal.
///
/// Written after a real failure: asked what was using the CPU, the model called
/// process_manager with action get_top_processes_by_cpu. The action is real and
/// the tool is real, but the action lives on performance, and the server's reply
/// - "Unknown action: get_top_processes_by_cpu" - says neither of those things.
/// </summary>
public class WindowsSystemActionsTests
{
    /// <summary>The real layout, as probed on 2026-09-25.</summary>
    private static WindowsSystemActions RealServer()
    {
        var actions = new WindowsSystemActions();

        actions.Add("process_manager", new[]
        {
            "list_processes", "get_process_details", "kill_process",
            "find_process", "get_top_processes", "get_process_tree"
        });

        actions.Add("performance", new[]
        {
            "get_cpu_usage", "get_memory_usage", "get_disk_usage", "get_disk_io",
            "get_network_io", "get_system_performance", "get_top_processes_by_cpu",
            "get_top_processes_by_memory", "get_performance_counters", "monitor_real_time"
        });

        return actions;
    }

    [Fact]
    public void The_failure_that_prompted_this_is_explained_and_redirected()
    {
        var explanation = RealServer().Explain(
            "process_manager",
            "Tool error: ❌ Process management operation failed: " +
            "Unknown action: get_top_processes_by_cpu");

        Assert.NotNull(explanation);

        // Where it actually lives, which is the one thing the model needs.
        Assert.Contains("performance", explanation);

        // And what it could have called on the tool it did pick, so a second
        // wrong guess is not the only way forward.
        Assert.Contains("get_top_processes", explanation);
    }

    [Fact]
    public void An_action_that_exists_nowhere_still_gets_the_valid_list()
    {
        var explanation = RealServer().Explain(
            "process_manager", "Unknown action: defragment_everything");

        Assert.NotNull(explanation);
        Assert.Contains("list_processes", explanation);

        // Nothing owns it, so nothing should be claimed to.
        Assert.DoesNotContain("belongs to", explanation);
    }

    [Fact]
    public void A_real_failure_is_left_exactly_as_the_server_wrote_it()
    {
        var actions = RealServer();

        // These are the answers to the question asked, not naming mistakes. A
        // model told to "try a different action" here would retry forever
        // instead of reporting what happened.
        Assert.Null(actions.Explain("process_manager", "No process found with PID 99999"));
        Assert.Null(actions.Explain("process_manager", "Access is denied"));
        Assert.Null(actions.Explain("filesystem", "Tool error: EPERM: operation not permitted"));
        Assert.Null(actions.Explain("process_manager", ""));
        Assert.Null(actions.Explain("process_manager", null));
    }

    [Fact]
    public void An_action_called_on_its_own_tool_is_not_redirected_to_itself()
    {
        // Should not happen - the server would have run it - but a message that
        // says "kill_process belongs to process_manager, call process_manager"
        // in reply to a process_manager call is worse than no message.
        var explanation = RealServer().Explain(
            "process_manager", "Unknown action: kill_process");

        Assert.NotNull(explanation);
        Assert.DoesNotContain("belongs to", explanation);
    }

    [Fact]
    public void A_tool_never_registered_still_explains_what_it_can()
    {
        var explanation = RealServer().Explain(
            "registry", "Unknown action: get_top_processes_by_cpu");

        // Nothing is known about registry's own actions, but the redirect is
        // still worth having.
        Assert.NotNull(explanation);
        Assert.Contains("performance", explanation);
        Assert.DoesNotContain("'registry' accepts", explanation);
    }

    [Fact]
    public void An_empty_map_explains_nothing_rather_than_guessing()
    {
        var actions = new WindowsSystemActions();

        Assert.True(actions.IsEmpty);

        // A server that published no action enums leaves nothing to say beyond
        // the bare fact, which the server already said.
        var explanation = actions.Explain("process_manager", "Unknown action: whatever");

        Assert.NotNull(explanation);
        Assert.DoesNotContain("belongs to", explanation);
        Assert.DoesNotContain("accepts", explanation);
    }

    [Fact]
    public void Adding_a_tool_with_no_actions_does_not_register_it()
    {
        var actions = new WindowsSystemActions();

        actions.Add("system_info", Array.Empty<string>());

        Assert.True(actions.IsEmpty);
    }
}
