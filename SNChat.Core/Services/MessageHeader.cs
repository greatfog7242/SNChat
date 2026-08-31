using System.Globalization;
using System.Text.RegularExpressions;
using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>
/// Reads and writes the header line that introduces each message in a stored
/// conversation:
///
///   ## Message 3 (Assistant) - 2026-08-31 12:18:23 [provider=Ollama; model=qwen2.5:7b; in=3659; out=2000]
///
/// Everything after the timestamp is optional and was added later, so headers
/// written before it exists still parse - they simply carry no counts. Kept
/// apart from StorageService so the format can be tested without touching the
/// filesystem, since that service writes to a fixed location under AppData.
/// </summary>
public static class MessageHeader
{
    /// <summary>
    /// Anchored to the shape of a timestamp rather than "everything after the
    /// dash", which would otherwise swallow the metadata that now follows it.
    /// </summary>
    private static readonly Regex TimestampPattern =
        new(@"-\s(\d{4}-\d{2}-\d{2}[ T]\d{2}:\d{2}:\d{2})", RegexOptions.Compiled);

    private static readonly Regex RolePattern =
        new(@"\((\w+)\)", RegexOptions.Compiled);

    private static readonly Regex MetadataPattern =
        new(@"\[([^\]]*)\]\s*$", RegexOptions.Compiled);

    public static string Format(int number, Message message)
    {
        // Normalised to UTC, so the stored value cannot depend on the timezone
        // of the machine that wrote it.
        var timestamp = message.Timestamp.ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var line = $"## Message {number} ({message.Role}) - {timestamp}";

        // Only fields that were actually measured are written, so an absent
        // figure stays absent on the way back in and reads as "n/a" rather than
        // as a measured zero.
        var fields = new List<string>();

        if (!string.IsNullOrEmpty(message.Provider))
            fields.Add($"provider={message.Provider}");

        if (!string.IsNullOrEmpty(message.ModelName))
            fields.Add($"model={message.ModelName}");

        if (message.PromptTokens.HasValue)
            fields.Add($"in={message.PromptTokens.Value}");

        if (message.CompletionTokens.HasValue)
            fields.Add($"out={message.CompletionTokens.Value}");

        if (message.ReasoningTokens.HasValue)
            fields.Add($"thinking={message.ReasoningTokens.Value}");

        if (message.Cost.HasValue)
            fields.Add($"cost={message.Cost.Value.ToString(CultureInfo.InvariantCulture)}");

        return fields.Count == 0 ? line : $"{line} [{string.Join("; ", fields)}]";
    }

    /// <summary>
    /// Reads a header with the "## Message " prefix already stripped, as the
    /// body is split on it. Returns false when the line carries no usable role,
    /// which is how a malformed block is skipped rather than half-imported.
    /// </summary>
    public static bool TryParse(string header, out MessageRole role, out DateTime timestamp, out MessageFacts facts)
    {
        role = default;
        timestamp = DateTime.UtcNow;
        facts = new MessageFacts();

        var roleMatch = RolePattern.Match(header);
        if (!roleMatch.Success || !Enum.TryParse(roleMatch.Groups[1].Value, true, out role))
            return false;

        var timestampMatch = TimestampPattern.Match(header);
        if (timestampMatch.Success)
        {
            // The stored form carries no timezone marker, so it is stated here
            // rather than inferred; everything written has always been UTC.
            timestamp = DateTime.Parse(
                timestampMatch.Groups[1].Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        }

        var metadataMatch = MetadataPattern.Match(header);
        if (metadataMatch.Success)
            facts = ParseFacts(metadataMatch.Groups[1].Value);

        return true;
    }

    private static MessageFacts ParseFacts(string metadata)
    {
        var facts = new MessageFacts();

        foreach (var field in metadata.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = field.IndexOf('=');
            if (separator < 0)
                continue;

            var key = field[..separator].Trim();
            var value = field[(separator + 1)..].Trim();

            switch (key)
            {
                case "provider":
                    facts.Provider = value;
                    break;
                case "model":
                    facts.ModelName = value;
                    break;
                case "in":
                    facts.PromptTokens = ParseInt(value);
                    break;
                case "out":
                    facts.CompletionTokens = ParseInt(value);
                    break;
                case "thinking":
                    facts.ReasoningTokens = ParseInt(value);
                    break;
                case "cost":
                    facts.Cost = decimal.TryParse(value, NumberStyles.Any,
                        CultureInfo.InvariantCulture, out var cost) ? cost : null;
                    break;
            }
        }

        return facts;

        static int? ParseInt(string value) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }
}

/// <summary>What a stored header records about a message beyond its text.</summary>
public class MessageFacts
{
    public string Provider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? ReasoningTokens { get; set; }
    public decimal? Cost { get; set; }
}
