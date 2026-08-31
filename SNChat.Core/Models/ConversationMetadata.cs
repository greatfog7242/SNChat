namespace SNChat.Core.Models;

public class ConversationMetadata
{
    public string ModelName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public ModelParameters Parameters { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> CustomData { get; set; } = new();

    /// <summary>
    /// Tokens this conversation has cost in total, accumulated across every
    /// turn and every session. Stored on the conversation rather than summed
    /// from its messages, because per-message counts are not persisted - so a
    /// reopened conversation would otherwise report nothing spent.
    /// </summary>
    public int TotalPromptTokens { get; set; }
    public int TotalCompletionTokens { get; set; }

    /// <summary>
    /// Charge accumulated in USD. Stays null while nothing has reported a cost,
    /// which distinguishes a conversation run entirely on local models from one
    /// that genuinely cost nothing.
    /// </summary>
    public decimal? TotalCost { get; set; }

    public ConversationMetadata Clone()
    {
        return new ConversationMetadata
        {
            ModelName = ModelName,
            Provider = Provider,
            Parameters = Parameters.Clone(),
            Tags = new List<string>(Tags),
            CustomData = new Dictionary<string, object>(CustomData),
            TotalPromptTokens = TotalPromptTokens,
            TotalCompletionTokens = TotalCompletionTokens,
            TotalCost = TotalCost
        };
    }
}
