namespace SNChat.LLM.Models;

public class Model
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public long ParameterSize { get; set; }
    public long ContextWindow { get; set; }
    public Dictionary<string, object> Capabilities { get; set; } = new();
}
