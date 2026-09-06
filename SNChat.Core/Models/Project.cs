namespace SNChat.Core.Models;

/// <summary>How much the assistant may do on its own before handing back.</summary>
public enum AutonomyMode
{
    /// <summary>One reply per message, as the app has always behaved.</summary>
    Manual,

    /// <summary>
    /// Keeps working, but stops before each step to show what it intends to do
    /// and wait for approval.
    /// </summary>
    StepApprove,

    /// <summary>
    /// Carries on until it reports the task finished or a budget runs out.
    /// </summary>
    FullAuto
}

/// <summary>
/// A folder the assistant works in, and the rules of engagement for working
/// there. Introduced because autonomy is a per-project decision: a scratch
/// folder is a reasonable place to let it run unattended, and a folder holding
/// something you care about is not.
///
/// Also the unit that grants permission. Everything the build and run tools may
/// touch has to sit inside a project root or an entry in
/// <see cref="BuildToolSettings.AllowedRoots"/>, so adding a project here is the
/// deliberate act that lets the assistant build and run inside it.
/// </summary>
public class Project
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>The folder itself. Everything beneath it is in scope.</summary>
    public string RootPath { get; set; } = string.Empty;

    /// <summary>
    /// Defaults to <see cref="AutonomyMode.StepApprove"/> rather than full
    /// automation. A model that loops confidently into wrong work does the most
    /// damage when nobody is watching, and how a given model behaves on a given
    /// project is not knowable in advance - watch it once, then decide.
    /// </summary>
    public AutonomyMode Autonomy { get; set; } = AutonomyMode.StepApprove;

    /// <summary>
    /// How many times it may continue on its own before stopping to report.
    /// A stop condition that does not depend on the model choosing to stop.
    /// </summary>
    public int MaxLoopIterations { get; set; } = 25;

    /// <summary>
    /// Wall-clock ceiling for one automatic run. Iterations alone are a poor
    /// budget when a single build can take minutes.
    /// </summary>
    public int MaxLoopMinutes { get; set; } = 30;

    /// <summary>
    /// Take a git checkpoint before an automatic run, and refuse to start one in
    /// a folder that is neither a clean repo nor a repo at all. Unattended work
    /// that writes files and runs programs needs a way back, and git is the way
    /// back that already exists.
    /// </summary>
    public bool RequireGitCheckpoint { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Free-form notes, shown in the UI. Not sent to the model.</summary>
    public string Notes { get; set; } = string.Empty;

    /// <summary>Path on disk; null for a project that has not been saved yet.</summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Whether the folder still exists. A project pointing at a folder that has
    /// been moved or deleted should be visible as broken rather than silently
    /// failing every tool call.
    /// </summary>
    public bool RootExists => !string.IsNullOrWhiteSpace(RootPath) && Directory.Exists(RootPath);
}
