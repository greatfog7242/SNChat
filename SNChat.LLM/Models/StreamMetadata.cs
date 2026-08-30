namespace SNChat.LLM.Models;

public class StreamMetadata
{
    public int? PromptEvalCount { get; set; }
    public int? EvalCount { get; set; }
    public double? TotalDuration { get; set; }
}
