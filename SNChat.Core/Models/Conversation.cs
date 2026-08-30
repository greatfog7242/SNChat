namespace SNChat.Core.Models;

public class Conversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "New Conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid? ParentBranchId { get; set; }
    public int BranchPoint { get; set; } = -1;
    public List<Message> Messages { get; set; } = new();
    public ConversationMetadata Metadata { get; set; } = new();
    public string? FilePath { get; set; }
}
