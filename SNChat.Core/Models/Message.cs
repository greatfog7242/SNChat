using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SNChat.Core.Models;

public class Message : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private int? _promptTokens;
    private int? _completionTokens;

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

    /// <summary>Reads as "3,659 in" or "2,000 out"; empty when unmeasured.</summary>
    public string TokenSummary => PromptTokens.HasValue
        ? $"{PromptTokens.Value:N0} in"
        : CompletionTokens.HasValue
            ? $"{CompletionTokens.Value:N0} out"
            : string.Empty;

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
            Provider = Provider,
            ModelName = ModelName
        };
    }
}
