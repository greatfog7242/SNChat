using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SNChat.Core.Models;

public class Message : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private int? _promptTokens;
    private int? _completionTokens;
    private int? _reasoningTokens;
    private decimal? _cost;
    private bool _isCompacted;

    public Guid Id { get; set; } = Guid.NewGuid();
    public MessageRole Role { get; set; }

    public string Content
    {
        get => _content;
        set
        {
            if (_content != value)
            {
                _content = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Always UTC. Use <see cref="LocalTimestamp"/> for display.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The timestamp in the machine's timezone. Message cards used to bind
    /// Timestamp directly and so showed UTC, disagreeing with the conversation
    /// list beside them, which already converted.
    /// </summary>
    public DateTime LocalTimestamp => Timestamp.ToLocalTime();
    public List<Attachment> Attachments { get; set; } = new();
    public int Index { get; set; }

    // Both are assigned once the reply finishes, by which point the message is
    // already on screen, so they have to raise change notifications or the
    // count would never appear.

    /// <summary>
    /// Tokens the provider charged for the prompt on the turn this message
    /// belongs to. Set on the user message, since that is the turn's input.
    /// Covers the whole prompt - system text, the entire history and any tool
    /// definitions - not just this message's own text.
    /// </summary>
    public int? PromptTokens
    {
        get => _promptTokens;
        set
        {
            if (_promptTokens == value)
                return;

            _promptTokens = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TokenSummary));
            OnPropertyChanged(nameof(HasTokenSummary));
        }
    }

    /// <summary>
    /// Tokens generated for this reply. For a reasoning model this includes the
    /// thinking that never appears in the answer, which is why the number can
    /// look far too large for the text shown.
    /// </summary>
    public int? CompletionTokens
    {
        get => _completionTokens;
        set
        {
            if (_completionTokens == value)
                return;

            _completionTokens = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TokenSummary));
            OnPropertyChanged(nameof(HasTokenSummary));
        }
    }

    /// <summary>
    /// Which provider and model produced this message. Set on replies only, and
    /// recorded per message rather than taken from the conversation because the
    /// model can be switched mid-conversation, so a single conversation-level
    /// value would misattribute every earlier reply.
    /// </summary>
    public string Provider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;

    /// <summary>Reads as "Ollama / qwen2.5:7b"; empty when unrecorded.</summary>
    public string ModelSummary =>
        string.IsNullOrEmpty(ModelName) ? string.Empty : $"{Provider} / {ModelName}";

    public bool HasModelSummary => !string.IsNullOrEmpty(ModelName);

    /// <summary>
    /// True once this message has been folded into a compaction summary, which
    /// leaves it out of the prompt from then on.
    ///
    /// Kept rather than deleted so that compacting stays readable and reversible:
    /// the original wording is still on screen and still in the saved file, and
    /// only the copy sent to the model is shortened. Set after the message is
    /// already displayed, so it has to notify.
    /// </summary>
    public bool IsCompacted
    {
        get => _isCompacted;
        set
        {
            if (_isCompacted == value)
                return;

            _isCompacted = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RoleLabel));
        }
    }

    /// <summary>
    /// True for the message a compaction produced. It stands in for everything
    /// marked <see cref="IsCompacted"/> before it, so it is sent while they are not.
    /// </summary>
    public bool IsCompactionSummary { get; set; }

    // A Role == Tool message records one complete exchange: what was called,
    // with what, and what came back. Kept as a single message rather than an
    // assistant/tool pair because that pairing is provider-specific - Ollama
    // matches a result to its call by tool name and OpenAI by an id - and each
    // provider expands this back into whichever shape it needs.

    /// <summary>Which tool was called. Empty on any other kind of message.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// The id the model gave the call. OpenAI-shaped APIs reject a tool result
    /// whose id does not match a call the assistant made, so it has to survive.
    /// Ollama supplies none and does not need one.
    /// </summary>
    public string ToolCallId { get; set; } = string.Empty;

    /// <summary>
    /// The arguments as JSON, needed to rebuild the assistant's original call.
    /// A tool result sent without the call that produced it is rejected outright
    /// by OpenAI-shaped APIs.
    /// </summary>
    public string ToolArguments { get; set; } = string.Empty;

    public bool IsToolExchange => Role == MessageRole.Tool;

    /// <summary>
    /// True for the nudge an unattended run sends itself to take another step.
    ///
    /// It has to go out as a user turn, because that is what the model answers -
    /// but it did not come from the user, and showing it as though it did is
    /// simply untrue. Whoever reads the conversation afterwards should be able
    /// to tell which turns were theirs.
    /// </summary>
    public bool IsAutoContinue { get; set; }

    /// <summary>The heading on the message card, which says more than the role alone.</summary>
    public string RoleLabel =>
        IsCompactionSummary ? "Summary of earlier messages"
        : IsAutoContinue ? "Continued automatically"
        : IsToolExchange ? $"Tool · {ToolName}"
        : IsCompacted ? $"{Role} · compacted"
        : Role.ToString();

    /// <summary>
    /// Reasoning tokens included in <see cref="CompletionTokens"/>. Null means
    /// the provider does not report the split, which is shown as "n/a" rather
    /// than hidden, so an absent figure is not mistaken for zero thinking.
    /// </summary>
    public int? ReasoningTokens
    {
        get => _reasoningTokens;
        set
        {
            if (_reasoningTokens == value)
                return;

            _reasoningTokens = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TokenSummary));
        }
    }

    /// <summary>Charge for this reply in USD; null where the provider is free or silent.</summary>
    public decimal? Cost
    {
        get => _cost;
        set
        {
            if (_cost == value)
                return;

            _cost = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TokenSummary));
        }
    }

    /// <summary>
    /// "3,659 in" for a prompt, or for a reply the total with its split and
    /// charge: "2,000 out · 1,842 thinking + 158 response · $0.0032", with
    /// "n/a" where the provider reports neither.
    /// </summary>
    public string TokenSummary
    {
        get
        {
            if (PromptTokens.HasValue)
                return $"{PromptTokens.Value:N0} in";

            if (!CompletionTokens.HasValue)
                return string.Empty;

            var total = CompletionTokens.Value;

            var split = ReasoningTokens.HasValue
                ? $"{ReasoningTokens.Value:N0} thinking + {total - ReasoningTokens.Value:N0} response"
                : "thinking n/a";

            var cost = Cost.HasValue ? $"${Cost.Value:0.######}" : "cost n/a";

            return $"{total:N0} out · {split} · {cost}";
        }
    }

    public bool HasTokenSummary => PromptTokens.HasValue || CompletionTokens.HasValue;

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public Message Clone()
    {
        return new Message
        {
            Id = Guid.NewGuid(),
            Role = Role,
            Content = Content,
            Timestamp = Timestamp,
            Attachments = Attachments.Select(a => new Attachment
            {
                Id = a.Id,
                FileName = a.FileName,
                FilePath = a.FilePath,
                Type = a.Type,
                FileSize = a.FileSize,
                MimeType = a.MimeType,
                ExtractedText = a.ExtractedText
            }).ToList(),
            Index = Index,
            PromptTokens = PromptTokens,
            CompletionTokens = CompletionTokens,
            ReasoningTokens = ReasoningTokens,
            Cost = Cost,
            Provider = Provider,
            ModelName = ModelName,
            IsCompacted = IsCompacted,
            IsCompactionSummary = IsCompactionSummary,
            ToolName = ToolName,
            ToolCallId = ToolCallId,
            ToolArguments = ToolArguments,
            IsAutoContinue = IsAutoContinue
        };
    }
}
