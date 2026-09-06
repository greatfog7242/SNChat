namespace SNChat.Core.Models;

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ParentBranchId { get; set; }
    public int BranchPoint { get; set; } = -1;

    /// <summary>
    /// The project this conversation is working in, or null for an ordinary
    /// chat. Decides which folder the build and run tools may touch and how much
    /// the assistant may do unattended.
    ///
    /// A real field rather than an entry in <see cref="ConversationMetadata.CustomData"/>,
    /// which is never written to the stored file and would be silently lost.
    /// </summary>
    public Guid? ProjectId { get; set; }

    /// <summary>
    /// The template's standing instruction for this conversation, sent ahead of
    /// every message. Stored here because it used to live only in the view model
    /// and was therefore lost on restart and whenever an old conversation was
    /// reopened - so a conversation carried on under instructions it no longer
    /// had, silently.
    ///
    /// Safe to keep in the frontmatter only since the reader stopped splitting
    /// on the "---" substring; a multi-line value used to tear the file in half.
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    /// <summary>Which template supplied it, for the label in the toolbar.</summary>
    public string TemplateName { get; set; } = string.Empty;
    public List<Message> Messages { get; set; } = new();
    public ConversationMetadata Metadata { get; set; } = new();
    public string? FilePath { get; set; }
}
