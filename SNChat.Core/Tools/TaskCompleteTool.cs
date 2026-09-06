using SNChat.Core.Services;

namespace SNChat.Core.Tools;

/// <summary>
/// How the assistant says it has finished, when it is working through a task on
/// its own. Calling this ends the run; not calling it means the run continues
/// until a budget stops it.
/// </summary>
public class TaskCompleteTool : ITool
{
    private readonly AgentSignals _signals;

    public string Name => "task_complete";

    public string Description =>
        "Call this when the task you were given is finished and you have checked " +
        "the result - not merely when you have described what to do. While you " +
        "are working on your own, the work continues until you call it.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["summary"] = new()
            {
                Type = "string",
                Description = "A short account of what you did and how you checked it."
            }
        },
        Required = new List<string> { "summary" }
    };

    public TaskCompleteTool(AgentSignals signals)
    {
        _signals = signals;
    }

    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var summary = arguments.TryGetValue("summary", out var raw)
            ? raw?.ToString()?.Trim() ?? string.Empty
            : string.Empty;

        _signals.SignalComplete(summary);

        // The model reads this, so it should not imply anything further is
        // expected of it in this run.
        return Task.FromResult("Task marked complete. Nothing further will be run.");
    }
}
