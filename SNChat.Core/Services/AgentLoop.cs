using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>Why a run stopped, or that it should keep going.</summary>
public enum LoopVerdict
{
    /// <summary>Carry on. In step-approve, only after the user says so.</summary>
    Continue,

    /// <summary>The assistant called task_complete.</summary>
    Finished,

    /// <summary>It ran out of steps.</summary>
    OutOfIterations,

    /// <summary>It ran out of time.</summary>
    OutOfTime,

    /// <summary>The user stopped it.</summary>
    Cancelled,

    /// <summary>The turn failed, so continuing would only compound it.</summary>
    Failed,

    /// <summary>This project does not work on its own.</summary>
    NotAutonomous
}

/// <summary>
/// Decides whether an automatic run should take another turn.
///
/// Kept apart from the view model and free of any dependency on it, because
/// "when does this stop" is the part that must not be wrong: a loop that fails
/// to stop keeps calling a paid model and keeps writing files, and one that
/// stops too eagerly is merely annoying. It is worth being able to test every
/// exit without running a conversation.
/// </summary>
public class AgentLoop
{
    private readonly Project? _project;
    private readonly DateTime _startedAt;

    /// <summary>How many turns have been taken since the run began.</summary>
    public int Iteration { get; private set; }

    public AgentLoop(Project? project, DateTime? startedAt = null)
    {
        _project = project;
        _startedAt = startedAt ?? DateTime.UtcNow;
    }

    public AutonomyMode Autonomy => _project?.Autonomy ?? AutonomyMode.Manual;

    /// <summary>Whether this project works on its own at all.</summary>
    public bool IsAutonomous => Autonomy != AutonomyMode.Manual;

    /// <summary>
    /// Whether to take another turn, having just finished one.
    ///
    /// The order of these checks is deliberate. Completion is asked about first
    /// so that a run which finished on its last permitted step is reported as
    /// finished rather than as having run out; that is the difference between
    /// "done" and "gave up" in what the user is told.
    /// </summary>
    public LoopVerdict Decide(bool completed, bool cancelled, bool failed, DateTime? now = null)
    {
        if (!IsAutonomous)
            return LoopVerdict.NotAutonomous;

        if (completed)
            return LoopVerdict.Finished;

        // Before the budgets: a user who pressed stop should be told they
        // stopped it, not that it ran out of time.
        if (cancelled)
            return LoopVerdict.Cancelled;

        // Continuing after an error usually means repeating it, and each repeat
        // costs another whole inference.
        if (failed)
            return LoopVerdict.Failed;

        if (Iteration >= MaxIterations)
            return LoopVerdict.OutOfIterations;

        if ((now ?? DateTime.UtcNow) - _startedAt >= MaxDuration)
            return LoopVerdict.OutOfTime;

        return LoopVerdict.Continue;
    }

    /// <summary>Records that another turn has been taken.</summary>
    public void CountIteration() => Iteration++;

    /// <summary>
    /// Clamped, because these come from a hand-editable project file. Zero or a
    /// negative would otherwise mean either "never run" or "run forever",
    /// depending on which check saw it first.
    /// </summary>
    public int MaxIterations => Math.Clamp(_project?.MaxLoopIterations ?? 0, 1, 500);

    public TimeSpan MaxDuration =>
        TimeSpan.FromMinutes(Math.Clamp(_project?.MaxLoopMinutes ?? 0, 1, 24 * 60));

    /// <summary>What the user is told when the run ends.</summary>
    public string Explain(LoopVerdict verdict, string summary) => verdict switch
    {
        LoopVerdict.Finished => string.IsNullOrWhiteSpace(summary)
            ? $"Finished after {Iteration} step(s)."
            : $"Finished after {Iteration} step(s): {summary}",

        LoopVerdict.OutOfIterations =>
            $"Stopped after {Iteration} step(s) without finishing — the limit for this " +
            "project. Raise it in Settings, or say what to do next.",

        LoopVerdict.OutOfTime =>
            $"Stopped after {MaxDuration.TotalMinutes:0} minute(s) without finishing — the " +
            "time limit for this project.",

        LoopVerdict.Cancelled => $"Stopped by you after {Iteration} step(s).",

        LoopVerdict.Failed =>
            $"Stopped after {Iteration} step(s) because the last turn failed.",

        _ => string.Empty
    };

    /// <summary>
    /// The nudge sent to take another turn. Deliberately plain: it should read
    /// as a continuation rather than as a new instruction that might be mistaken
    /// for a change of task.
    /// </summary>
    public const string ContinuePrompt =
        "Continue. When the task is genuinely finished and you have checked the " +
        "result, call task_complete.";
}
