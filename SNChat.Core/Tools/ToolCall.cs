namespace SNChat.Core.Tools;

/// <summary>A request from the model to invoke a tool.</summary>
public class ToolCall
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, object?> Arguments { get; set; } = new();
}

/// <summary>The outcome of running a <see cref="ToolCall"/>.</summary>
public class ToolResult
{
    public string ToolCallId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public bool IsError { get; set; }
}
