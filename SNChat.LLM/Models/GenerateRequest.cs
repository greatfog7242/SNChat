using SNChat.Core.Models;

namespace SNChat.LLM.Models;

public class GenerateRequest
{
    public string Model { get; set; } = string.Empty;
    public List<Message> Messages { get; set; } = new();
    public ModelParameters Parameters { get; set; } = new();
    public string? SystemPrompt { get; set; }
    public CancellationToken CancellationToken { get; set; } = default;
}
