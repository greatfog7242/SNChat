using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;

namespace SNChat.LLM.Services;

/// <summary>
/// Folds the older part of a conversation into a single summary message, so
/// that a long conversation keeps fitting in the model's context window.
///
/// The messages it replaces are marked rather than deleted - see
/// <see cref="Message.IsCompacted"/> - so the conversation still reads in full
/// on screen and in its saved file. Only the copy sent to the model is shorter.
/// </summary>
public class ConversationCompactor
{
    /// <summary>
    /// Below this there is nothing to gain: a summary of one message is about
    /// as long as the message, and can easily come out longer.
    /// </summary>
    public const int MinimumToFold = 2;

    private const string Instruction =
        "Summarize the conversation above so that the summary can take its " +
        "place. Later messages will be answered from your summary alone, so " +
        "keep what they will need: what the user is trying to do, decisions and " +
        "conclusions already reached, facts established, and the names, files, " +
        "figures and identifiers that were referred to. Drop pleasantries, " +
        "restatements, and lines of reasoning that were abandoned. Write terse " +
        "notes rather than prose, under 400 words, and write nothing but the " +
        "summary itself.";

    private readonly ILogger<ConversationCompactor> _logger;

    public ConversationCompactor(ILogger<ConversationCompactor> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Which of the live messages should be folded away: the oldest ones, less
    /// the <paramref name="keepRecent"/> most recent, which are left alone
    /// because the exchange in progress is what the next answer leans on most.
    /// Empty when there is too little history to be worth folding.
    /// </summary>
    public static List<Message> Foldable(IReadOnlyList<Message> live, int keepRecent)
    {
        var foldCount = live.Count - Math.Max(0, keepRecent);

        return foldCount < MinimumToFold
            ? new List<Message>()
            : live.Take(foldCount).ToList();
    }

    /// <summary>
    /// Asks the model to summarize <paramref name="toFold"/> and returns the
    /// summary as a message ready to be put in their place, or null if the model
    /// gave nothing back. Never throws for a failed summary: not compacting
    /// leaves the conversation exactly as it was, which is a worse prompt but
    /// not a broken one.
    /// </summary>
    public async Task<Message?> SummarizeAsync(
        ILLMProvider provider,
        string model,
        IReadOnlyList<Message> toFold,
        CancellationToken cancellationToken = default)
    {
        if (toFold.Count == 0)
            return null;

        // The transcript goes in as one user message with the instruction last,
        // rather than as the messages themselves. Handed the real conversation,
        // a model tends to answer the final question in it instead of
        // summarizing anything.
        var request = new GenerateRequest
        {
            Model = model,
            Messages = new List<Message>
            {
                new()
                {
                    Role = MessageRole.User,
                    Content = Transcribe(toFold) + "\n\n---\n\n" + Instruction
                }
            },
            Parameters = new ModelParameters
            {
                // Low, because this is a transcription task: the summary should
                // report what was said, not embroider it.
                Temperature = 0.2,
                MaxTokens = 1024
            },
            // No tools. A summary needs nothing looked up, and their definitions
            // would only add to the prompt this is trying to shrink.
            Tools = Array.Empty<Core.Tools.ITool>(),
            CancellationToken = cancellationToken
        };

        var summary = new StringBuilder();

        try
        {
            await foreach (var chunk in provider.GenerateStreamAsync(request))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Progress notices are not part of the answer.
                if (!chunk.IsStatus)
                    summary.Append(chunk.Content);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not summarize {Count} messages for compaction", toFold.Count);
            return null;
        }

        var text = summary.ToString().Trim();

        if (string.IsNullOrEmpty(text))
        {
            _logger.LogWarning("Compaction produced an empty summary; leaving the conversation as it was");
            return null;
        }

        _logger.LogInformation("Compacted {Count} messages into a {Length}-character summary",
            toFold.Count, text.Length);

        return new Message
        {
            Role = MessageRole.System,
            Content = text,
            Timestamp = DateTime.UtcNow,
            IsCompactionSummary = true
        };
    }

    /// <summary>Renders messages as a labelled transcript for the model to read.</summary>
    private static string Transcribe(IReadOnlyList<Message> messages)
    {
        var transcript = new StringBuilder();

        foreach (var message in messages)
        {
            // An earlier summary is folded in like anything else, which is what
            // stops summaries stacking up one per compaction.
            var label = message.IsCompactionSummary
                ? "Summary of earlier messages"
                : message.Role.ToString();

            // "\n" rather than AppendLine: the prompt should not differ by the
            // platform the app happens to be running on.
            transcript.Append('[').Append(label).Append("]\n");
            transcript.Append(message.Content).Append("\n\n");
        }

        return transcript.ToString().TrimEnd();
    }
}
