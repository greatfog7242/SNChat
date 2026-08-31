namespace SNChat.LLM.Models;

public class StreamMetadata
{
    public int? PromptEvalCount { get; set; }

    /// <summary>Total generated tokens, reasoning included.</summary>
    public int? EvalCount { get; set; }

    /// <summary>
    /// The share of <see cref="EvalCount"/> spent reasoning rather than
    /// answering. Null where the provider does not break it out - Ollama
    /// reports only a single total, so the split is genuinely unavailable
    /// rather than merely unread.
    /// </summary>
    public int? ReasoningTokens { get; set; }

    /// <summary>Charge for the request in USD, where the provider bills.</summary>
    public decimal? Cost { get; set; }

    public double? TotalDuration { get; set; }
}
