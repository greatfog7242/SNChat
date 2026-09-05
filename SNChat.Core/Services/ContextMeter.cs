using System.Text.Json;
using SNChat.Core.Models;
using SNChat.Core.Tools;

namespace SNChat.Core.Services;

/// <summary>
/// How much of a model's context window the next request will take up.
/// </summary>
/// <param name="UsedTokens">
/// Tokens the prompt is expected to carry: system text, the whole history that
/// has not been compacted away, and whatever has been typed since.
/// </param>
/// <param name="WindowTokens">
/// What the model can hold. Zero means unknown, which leaves nothing to measure
/// against and so reports no usage at all rather than a made-up figure.
/// </param>
public readonly record struct ContextUsage(int UsedTokens, int WindowTokens)
{
    public bool IsKnown => WindowTokens > 0;

    /// <summary>
    /// Capped at 1. A prompt can genuinely exceed the window - that is the
    /// request the provider is about to reject - but as a bar reading there is
    /// nothing past full to show.
    /// </summary>
    public double Fraction => IsKnown
        ? Math.Min(1.0, (double)UsedTokens / WindowTokens)
        : 0;

    public int Percent => (int)Math.Round(Fraction * 100);

    /// <summary>Whether the window is as full as the given percentage.</summary>
    public bool HasReached(int thresholdPercent) =>
        IsKnown && Percent >= thresholdPercent;
}

/// <summary>
/// Estimates prompt size from text, for use between the turns that measure it
/// for real. Deliberately crude: the tokenizer differs per model and is not
/// available here, and a meter that is a few percent out is still the
/// difference between seeing a window fill up and being surprised by it.
/// </summary>
public static class TokenEstimator
{
    /// <summary>
    /// English prose runs roughly four characters to the token. Code and other
    /// punctuation-heavy text runs denser, so this reads low on a coding
    /// conversation - the first real measurement corrects it.
    /// </summary>
    public const int CharactersPerToken = 4;

    /// <summary>
    /// Role names and delimiters each message is wrapped in before it is sent.
    /// Small, but a long conversation is mostly short messages.
    /// </summary>
    public const int PerMessageOverhead = 4;

    public static int Estimate(string? text) =>
        string.IsNullOrEmpty(text)
            ? 0
            : (text.Length + CharactersPerToken - 1) / CharactersPerToken;

    public static int Estimate(IEnumerable<Message> messages) =>
        messages.Sum(m => Estimate(m.Content) + PerMessageOverhead);

    /// <summary>
    /// What the tool definitions add to every request that carries them.
    ///
    /// Easily the largest term once MCP servers are connected: nineteen tools
    /// measured at about sixteen thousand tokens, against a hundred and thirty
    /// for the message that prompted them. Leaving these out made the meter read
    /// near zero right up until the provider's first real count arrived, at
    /// which point it jumped straight to full.
    ///
    /// The name, description and parameter schema are what each provider sends;
    /// they are measured as JSON because that is the form they go over the wire in.
    /// </summary>
    public static int EstimateTools(IEnumerable<ITool> tools)
    {
        var total = 0;

        foreach (var tool in tools)
        {
            total += Estimate(tool.Name) + Estimate(tool.Description);

            try
            {
                total += Estimate(JsonSerializer.Serialize(tool.Parameters));
            }
            catch
            {
                // A schema that will not serialize is a tool the provider cannot
                // send either. Counting nothing for it is closer than refusing
                // to measure the rest.
            }
        }

        return total;
    }
}

public static class ContextMeter
{
    /// <summary>
    /// The messages a request actually carries: everything a compaction has
    /// folded away is left out, while the summary standing in for them is not.
    /// </summary>
    public static List<Message> LiveMessages(IEnumerable<Message> messages) =>
        messages.Where(m => !m.IsCompacted).ToList();

    /// <summary>
    /// What the next request will cost in context.
    ///
    /// Prefers the provider's own count over an estimate wherever it can:
    /// <paramref name="measuredPromptTokens"/> is what was charged for a real
    /// prompt covering the first <paramref name="measuredThrough"/> of these
    /// messages, and only what came after it is estimated. That count includes
    /// the system prompt and any tool definitions, neither of which this can see
    /// - so with no measurement to build on the estimate reads low.
    /// </summary>
    /// <param name="messages">Live messages, in order; see <see cref="LiveMessages"/>.</param>
    /// <param name="systemPrompt">Sent ahead of them, and counted only while estimating.</param>
    /// <param name="windowTokens">The model's capacity, or 0 if unknown.</param>
    /// <param name="measuredPromptTokens">Prompt tokens the provider reported, if any.</param>
    /// <param name="measuredThrough">How many leading messages that count covered.</param>
    /// <param name="toolTokens">
    /// What the tool definitions cost, from <see cref="TokenEstimator.EstimateTools"/>,
    /// or 0 when the request will carry none. Counted only while estimating, since a
    /// measured prompt already includes them.
    /// </param>
    public static ContextUsage Measure(
        IReadOnlyList<Message> messages,
        string? systemPrompt,
        int windowTokens,
        int? measuredPromptTokens = null,
        int measuredThrough = 0,
        int toolTokens = 0)
    {
        // A measurement that reaches past the end of the list describes a
        // history that no longer exists - the usual cause is the compaction it
        // prompted, which removed the very messages it counted. Estimating the
        // whole thing is the only honest reading left.
        var usable = measuredPromptTokens is > 0
            && measuredThrough > 0
            && measuredThrough <= messages.Count;

        var used = usable
            ? measuredPromptTokens!.Value + TokenEstimator.Estimate(messages.Skip(measuredThrough))
            : TokenEstimator.Estimate(systemPrompt) + TokenEstimator.Estimate(messages) + toolTokens;

        return new ContextUsage(used, windowTokens);
    }
}
