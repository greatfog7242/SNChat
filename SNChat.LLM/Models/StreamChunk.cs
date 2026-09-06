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

    /// <summary>
    /// A tool that has just run, and what it returned. Emitted so the app can
    /// keep it with the conversation: the provider's own copy lives only inside
    /// one request and is discarded when the reply finishes, which loses every
    /// file read and every build run as soon as the turn ends.
    /// </summary>
    public ToolExchange? ToolExchange { get; set; }
}

/// <summary>One completed tool call, as it should be remembered.</summary>
public class ToolExchange
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Empty for providers that do not use ids, such as Ollama.</summary>
    public string CallId { get; set; } = string.Empty;

    /// <summary>The arguments as JSON, needed to rebuild the call later.</summary>
    public string Arguments { get; set; } = string.Empty;

    public string Result { get; set; } = string.Empty;
}
