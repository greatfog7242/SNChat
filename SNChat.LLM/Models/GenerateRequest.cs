using SNChat.Core.Models;
using SNChat.Core.Tools;

namespace SNChat.LLM.Models;

public class GenerateRequest
{
    public string Model { get; set; } = string.Empty;
    public List<Message> Messages { get; set; } = new();
    public ModelParameters Parameters { get; set; } = new();
    public string? SystemPrompt { get; set; }
    public CancellationToken CancellationToken { get; set; } = default;

    /// <summary>
    /// Tools the model may invoke. When empty, no tool definitions are sent,
    /// which keeps the prompt smaller for ordinary chats.
    /// </summary>
    public IReadOnlyList<ITool> Tools { get; set; } = Array.Empty<ITool>();

    /// <summary>
    /// Caps how many times the model may call tools before it must answer.
    /// Guards against a model looping on tool calls indefinitely.
    /// </summary>
    public int MaxToolIterations { get; set; } = 5;
}
