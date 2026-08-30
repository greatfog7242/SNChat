namespace SNChat.LLM.Models;

public class StreamChunk
{
    public string Content { get; set; } = string.Empty;
    public bool IsFinal { get; set; }
    public StreamMetadata? Metadata { get; set; }
}
