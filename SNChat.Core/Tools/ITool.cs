namespace SNChat.Core.Tools;

/// <summary>
/// A capability the model can invoke mid-conversation, such as a web search.
/// Implementations must be safe to call concurrently.
/// </summary>
public interface ITool
{
    /// <summary>Identifier the model uses to call this tool. Must be unique.</summary>
    string Name { get; }

    /// <summary>
    /// Describes when the tool should be used. The model relies on this text to
    /// decide whether to call it, so it should be specific about what the tool
    /// is good for and what it returns.
    /// </summary>
    string Description { get; }

    /// <summary>JSON Schema describing the arguments this tool accepts.</summary>
    ToolParameterSchema Parameters { get; }

    /// <summary>
    /// Runs the tool. Implementations should return a human-readable string that
    /// gets fed back to the model. Errors should be returned as descriptive text
    /// rather than thrown, so the model can recover or explain the failure.
    /// </summary>
    Task<string> ExecuteAsync(IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default);
}
