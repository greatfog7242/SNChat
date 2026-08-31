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

    /// <summary>
    /// Downscaled copy used when sending the image to a model. Null when the
    /// original is already small enough, in which case FilePath is sent.
    /// </summary>
    public string? ModelImagePath { get; set; }

    /// <summary>The image a model should be given: the smaller copy if one exists.</summary>
    public string ImagePathForModel => ModelImagePath ?? FilePath;
}
