using Microsoft.Extensions.Logging;

namespace SNChat.Core.Tools;

public class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<ToolRegistry> _logger;

    public ToolRegistry(ILogger<ToolRegistry> logger)
    {
        _logger = logger;
    }

    public void Register(ITool tool)
    {
        _tools[tool.Name] = tool;
        _logger.LogInformation("Registered tool: {ToolName}", tool.Name);
    }

    public IReadOnlyList<ITool> GetTools() => _tools.Values.ToList();

    public ITool? GetTool(string name) =>
        _tools.TryGetValue(name, out var tool) ? tool : null;

    public async Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
    {
        var tool = GetTool(call.Name);
        if (tool == null)
        {
            _logger.LogWarning("Model requested unknown tool: {ToolName}", call.Name);
            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Name,
                Content = $"No tool named '{call.Name}' is available.",
                IsError = true
            };
        }

        try
        {
            _logger.LogInformation("Executing tool {ToolName} with {ArgCount} argument(s)",
                call.Name, call.Arguments.Count);

            var content = await tool.ExecuteAsync(call.Arguments, cancellationToken);

            // The head of the result, which is where a tool that refused says
            // why. Only the invocation was logged before, so a tool failing
            // every time was invisible here and showed up only as the model
            // giving up and trying something else.
            _logger.LogDebug("Tool {ToolName} returned: {Result}",
                call.Name,
                content.Length <= 300 ? content : content[..300] + "...");

            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Name,
                Content = content
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Surface failures to the model as text so it can explain or retry,
            // rather than aborting the whole response.
            _logger.LogError(ex, "Tool {ToolName} threw", call.Name);
            return new ToolResult
            {
                ToolCallId = call.Id,
                Name = call.Name,
                Content = $"The tool failed: {ex.Message}",
                IsError = true
            };
        }
    }
}
