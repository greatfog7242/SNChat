namespace SNChat.Core.Models;

/// <summary>
/// A folder of conversations, made and named by the user.
///
/// Membership is held here rather than in each conversation's frontmatter
/// because saving a conversation stamps it with a fresh UpdatedAt: recording a
/// group there would re-date the conversation and throw it to the top of the
/// list every time it was dragged. Nothing about grouping rewrites a
/// conversation file.
/// </summary>
public class ConversationGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    /// <summary>Remembered across runs, so a group folded away stays folded.</summary>
    public bool IsExpanded { get; set; } = true;

    /// <summary>
    /// The conversations filed here. A conversation belongs to at most one
    /// group; keeping that true is the job of the service, not the caller.
    /// </summary>
    public List<Guid> ConversationIds { get; set; } = new();
}
