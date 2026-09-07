namespace SNChat.Core.Models;

/// <summary>
/// A named assistant that can be handed a piece of work to do on its own.
///
/// The point is context, not capability. A subagent works in a conversation of
/// its own and returns only what it concluded, so a job that takes twenty tool
/// calls costs the main conversation one answer rather than twenty exchanges.
/// Without that, a long task fills the window and starts compacting away the
/// very findings it was sent to gather.
///
/// Stored as markdown with YAML frontmatter, like projects and templates, so
/// they stay hand-editable.
/// </summary>
public class AgentDefinition
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The name the assistant uses to ask for it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What it is for. This is what the calling assistant reads when deciding
    /// whether to delegate, so it should describe the job rather than the
    /// method.
    /// </summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>
    /// Which tools it may use, by name. Empty means every tool the main
    /// assistant has, minus the ones that are never delegated.
    ///
    /// Worth narrowing: a subagent sent to read and report does not need to be
    /// able to build, run or commit, and a smaller tool set is both a smaller
    /// prompt and a smaller blast radius.
    /// </summary>
    public List<string> AllowedTools { get; set; } = new();

    /// <summary>
    /// A model to use instead of whichever the conversation is on. Empty means
    /// the same one. Useful for sending cheap, repetitive work to a small local
    /// model while the main conversation runs on something stronger.
    /// </summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// How many rounds of tool calls it may make before it has to answer. Its
    /// own budget, because a subagent that searches ten files is doing its job
    /// while the main conversation would be looping.
    /// </summary>
    public int MaxToolIterations { get; set; } = 10;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Its standing instruction - the body of the file.</summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Path on disk; null for one that has not been saved yet.</summary>
    public string? FilePath { get; set; }
}
