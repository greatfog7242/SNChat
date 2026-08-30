namespace SNChat.LLM.Models;

public class StreamChunk
{
    public string Content { get; set; } = string.Empty;
    public bool IsFinal { get; set; }
    public StreamMetadata? Metadata { get; set; }

    /// <summary>
    /// Set for progress notices such as "searching the web" that should be shown
    /// to the user but not persisted as part of the assistant's answer.
    /// </summary>
    public bool IsStatus { get; set; }
}
