using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.Tests;

/// <summary>
/// When an unattended run stops is the part that must not be wrong. A loop that
/// fails to stop keeps calling a paid model and keeps writing files; one that
/// stops too eagerly is merely irritating. Every exit is worth testing without
/// running a conversation to reach it.
/// </summary>
public class AgentLoopTests
{
    private static Project Autonomous(
        AutonomyMode mode = AutonomyMode.FullAuto,
        int iterations = 25,
        int minutes = 30) =>
        new() { Autonomy = mode, MaxLoopIterations = iterations, MaxLoopMinutes = minutes };

    private static AgentLoop Loop(Project? project, DateTime? startedAt = null) =>
        new(project, startedAt ?? new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc));

    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_manual_project_never_runs_on_its_own()
    {
        // The default, and today's behaviour for every existing conversation.
        var loop = Loop(Autonomous(AutonomyMode.Manual));

        Assert.False(loop.IsAutonomous);
        Assert.Equal(LoopVerdict.NotAutonomous,
            loop.Decide(completed: false, cancelled: false, failed: false, Now));
    }

    [Fact]
    public void A_conversation_with_no_project_never_runs_on_its_own()
    {
        Assert.Equal(LoopVerdict.NotAutonomous,
            Loop(null).Decide(completed: false, cancelled: false, failed: false, Now));
    }

    [Fact]
    public void It_carries_on_while_there_is_budget_and_nothing_has_gone_wrong()
    {
        Assert.Equal(LoopVerdict.Continue,
            Loop(Autonomous()).Decide(completed: false, cancelled: false, failed: false, Now));
    }

    [Fact]
    public void Calling_task_complete_ends_the_run()
    {
        Assert.Equal(LoopVerdict.Finished,
            Loop(Autonomous()).Decide(completed: true, cancelled: false, failed: false, Now));
    }

    [Fact]
    public void Finishing_on_the_last_permitted_step_is_reported_as_finished()
    {
        // The difference between "done" and "gave up", which is why completion
        // is asked about before the budgets.
        var loop = Loop(Autonomous(iterations: 2));
        loop.CountIteration();
        loop.CountIteration();

        Assert.Equal(LoopVerdict.Finished,
            loop.Decide(completed: true, cancelled: false, failed: false, Now));
    }

    [Fact]
    public void Running_out_of_steps_stops_it()
    {
        var loop = Loop(Autonomous(iterations: 3));

        for (var i = 0; i < 3; i++)
            loop.CountIteration();

        Assert.Equal(LoopVerdict.OutOfIterations,
            loop.Decide(completed: false, cancelled: false, failed: false, Now));
    }

    [Fact]
    public void Running_out_of_time_stops_it()
    {
        var loop = Loop(Autonomous(minutes: 10), Now);

        Assert.Equal(LoopVerdict.OutOfTime,
            loop.Decide(completed: false, cancelled: false, failed: false, Now.AddMinutes(11)));
    }

    [Fact]
    public void A_long_running_build_does_not_stop_it_early()
    {
        var loop = Loop(Autonomous(minutes: 30), Now);

        Assert.Equal(LoopVerdict.Continue,
            loop.Decide(completed: false, cancelled: false, failed: false, Now.AddMinutes(29)));
    }

    [Fact]
    public void Stopping_it_is_reported_as_stopping_rather_than_as_a_budget()
    {
        // A user who pressed stop should be told they stopped it.
        var loop = Loop(Autonomous(minutes: 1), Now);

        Assert.Equal(LoopVerdict.Cancelled,
            loop.Decide(completed: false, cancelled: true, failed: false, Now.AddMinutes(5)));
    }

    [Fact]
    public void A_failed_turn_stops_the_run_rather_than_repeating_itself()
    {
        // Continuing after an error usually repeats it, and each repeat costs
        // another whole inference.
        Assert.Equal(LoopVerdict.Failed,
            Loop(Autonomous()).Decide(completed: false, cancelled: false, failed: true, Now));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_nonsensical_step_limit_is_clamped_rather_than_meaning_forever(int configured)
    {
        // These come from a hand-editable project file, where zero could mean
        // either "never run" or "run without limit" depending on which check saw
        // it first.
        var loop = Loop(Autonomous(iterations: configured));

        Assert.True(loop.MaxIterations >= 1);

        loop.CountIteration();
        Assert.Equal(LoopVerdict.OutOfIterations,
            loop.Decide(completed: false, cancelled: false, failed: false, Now));
    }

    [Fact]
    public void An_absurd_step_limit_is_capped()
    {
        Assert.True(Loop(Autonomous(iterations: 100000)).MaxIterations <= 500);
    }

    [Fact]
    public void Each_ending_is_explained_in_terms_the_user_can_act_on()
    {
        var loop = Loop(Autonomous(iterations: 4));
        loop.CountIteration();

        Assert.Contains("Finished", loop.Explain(LoopVerdict.Finished, "built and ran it"));
        Assert.Contains("built and ran it", loop.Explain(LoopVerdict.Finished, "built and ran it"));
        Assert.Contains("Settings", loop.Explain(LoopVerdict.OutOfIterations, ""));
        Assert.Contains("time limit", loop.Explain(LoopVerdict.OutOfTime, ""));
        Assert.Contains("Stopped by you", loop.Explain(LoopVerdict.Cancelled, ""));
        Assert.Contains("failed", loop.Explain(LoopVerdict.Failed, ""));
    }

    // --- the completion signal ---

    [Fact]
    public void Completion_is_read_once_and_then_forgotten()
    {
        // Consuming rather than peeking: a signal left over from one run must
        // not stop the next before it has done anything.
        var signals = new AgentSignals();
        signals.SignalComplete("done");

        Assert.True(signals.ConsumeComplete());
        Assert.False(signals.ConsumeComplete());
    }

    [Fact]
    public async Task The_tool_records_what_the_assistant_says_it_did()
    {
        var signals = new AgentSignals();

        var answer = await new TaskCompleteTool(signals).ExecuteAsync(
            new Dictionary<string, object?> { ["summary"] = "Built it and ran the tests." });

        Assert.True(signals.ConsumeComplete());
        Assert.Equal("Built it and ran the tests.", signals.Summary);

        // What the model reads back should not suggest more is expected of it.
        Assert.Contains("complete", answer);
    }

    [Fact]
    public async Task Completion_with_no_summary_still_stops_the_run()
    {
        var signals = new AgentSignals();

        await new TaskCompleteTool(signals).ExecuteAsync(new Dictionary<string, object?>());

        Assert.True(signals.ConsumeComplete());
    }

    // --- the checkpoint, against real git ---

    [Fact]
    public async Task A_folder_that_is_not_a_repository_will_not_be_worked_on_unattended()
    {
        // The refusal is the point: an automatic run over a folder with no
        // history has nothing to undo to.
        var folder = Path.Combine(Path.GetTempPath(), "snchat-nogit-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        try
        {
            var result = await Checkpoint().PrepareAsync(folder, required: true);

            Assert.False(result.CanProceed);
            Assert.Contains("not a git repository", result.Message);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Uncommitted_changes_stop_an_unattended_run_from_starting()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snchat-dirty-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        try
        {
            if (!await InitRepo(folder))
                return;

            await File.WriteAllTextAsync(Path.Combine(folder, "uncommitted.txt"), "not staged");

            var result = await Checkpoint().PrepareAsync(folder, required: true);

            Assert.False(result.CanProceed);
            Assert.Contains("uncommitted", result.Message);
        }
        finally
        {
            DeleteRepo(folder);
        }
    }

    [Fact]
    public async Task A_clean_repository_yields_a_commit_to_go_back_to()
    {
        var folder = Path.Combine(Path.GetTempPath(), "snchat-clean-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);

        try
        {
            if (!await InitRepo(folder))
                return;

            var result = await Checkpoint().PrepareAsync(folder, required: true);

            Assert.True(result.CanProceed, result.Message);
            Assert.False(string.IsNullOrEmpty(result.Commit));

            // The message has to contain the command to undo the run, or the
            // checkpoint is only theoretically a way back.
            Assert.Contains("reset --hard", result.Message);
        }
        finally
        {
            DeleteRepo(folder);
        }
    }

    [Fact]
    public async Task Turning_the_checkpoint_off_skips_all_of_it()
    {
        var result = await Checkpoint().PrepareAsync("nowhere at all", required: false);

        Assert.True(result.CanProceed);
    }

    private static SNChat.BuildTools.GitCheckpointService Checkpoint() =>
        new(new SNChat.BuildTools.ProcessRunner(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<SNChat.BuildTools.ProcessRunner>.Instance),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SNChat.BuildTools.GitCheckpointService>.Instance);

    /// <summary>A repository with one commit, or false when git is unavailable.</summary>
    private static async Task<bool> InitRepo(string folder)
    {
        var git = SNChat.BuildTools.ToolchainLocator.FindOnPath("git");

        if (git == null)
            return false;

        var runner = new SNChat.BuildTools.ProcessRunner(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SNChat.BuildTools.ProcessRunner>.Instance);

        var timeout = TimeSpan.FromSeconds(30);

        await runner.RunAsync(git, new[] { "init" }, folder, timeout);
        await runner.RunAsync(git, new[] { "config", "user.email", "t@example.com" }, folder, timeout);
        await runner.RunAsync(git, new[] { "config", "user.name", "Test" }, folder, timeout);

        await File.WriteAllTextAsync(Path.Combine(folder, "committed.txt"), "content");

        await runner.RunAsync(git, new[] { "add", "." }, folder, timeout);
        var commit = await runner.RunAsync(git, new[] { "commit", "-m", "first" }, folder, timeout);

        return commit.Succeeded;
    }

    private static void DeleteRepo(string folder)
    {
        try
        {
            // Git marks objects read-only, which blocks a plain recursive delete.
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Resetting_clears_a_signal_left_over_from_an_earlier_run()
    {
        var signals = new AgentSignals();
        signals.SignalComplete("stale");
        signals.Reset();

        Assert.False(signals.ConsumeComplete());
        Assert.Equal(string.Empty, signals.Summary);
    }
}
