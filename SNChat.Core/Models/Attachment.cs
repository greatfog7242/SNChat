namespace SNChat.Core.Models;

public class Attachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public AttachmentType Type { get; set; }
    public long FileSize { get; set; }
    public string MimeType { get; set; } = string.Empty;
    public string? ExtractedText { get; set; }
}
