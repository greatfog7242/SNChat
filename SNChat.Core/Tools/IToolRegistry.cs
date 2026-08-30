namespace SNChat.Core.Tools;

/// <summary>
/// Holds the tools available to the model and dispatches calls to them.
/// </summary>
public interface IToolRegistry
{
    IReadOnlyList<ITool> GetTools();
    ITool? GetTool(string name);
    void Register(ITool tool);

    /// <summary>
    /// Looks up and runs the requested tool. Unknown tool names and thrown
    /// exceptions both come back as <see cref="ToolResult.IsError"/> results so
    /// the conversation can continue instead of tearing down.
    /// </summary>
    Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default);
}
