using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SNChat.Core.Models;

public class Message : INotifyPropertyChanged
{
    private string _content = string.Empty;

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

    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public List<Attachment> Attachments { get; set; } = new();
    public int Index { get; set; }

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
            Index = Index
        };
    }
}
