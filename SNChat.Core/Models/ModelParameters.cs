namespace SNChat.Core.Models;

public class ModelParameters
{
    public double Temperature { get; set; } = 0.7;
    public int MaxTokens { get; set; } = 2048;
    public double TopP { get; set; } = 0.9;
    public double FrequencyPenalty { get; set; } = 0.0;
    public double PresencePenalty { get; set; } = 0.0;
    public List<string>? StopSequences { get; set; }

    public ModelParameters Clone()
    {
        return new ModelParameters
        {
            Temperature = Temperature,
            MaxTokens = MaxTokens,
            TopP = TopP,
            FrequencyPenalty = FrequencyPenalty,
            PresencePenalty = PresencePenalty,
            StopSequences = StopSequences?.ToList()
        };
    }
}
