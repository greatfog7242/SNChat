namespace SNChat.Core.Models;

public class ConversationMetadata
{
    public string ModelName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public ModelParameters Parameters { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, object> CustomData { get; set; } = new();

    public ConversationMetadata Clone()
    {
        return new ConversationMetadata
        {
            ModelName = ModelName,
            Provider = Provider,
            Parameters = Parameters.Clone(),
            Tags = new List<string>(Tags),
            CustomData = new Dictionary<string, object>(CustomData)
        };
    }
}
