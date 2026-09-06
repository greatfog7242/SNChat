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
    public List<Message> Messages { get; set; } = new();
    public ConversationMetadata Metadata { get; set; } = new();
    public string? FilePath { get; set; }
}
