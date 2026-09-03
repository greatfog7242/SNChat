using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;

namespace SNChat.App.ViewModels;

public partial class ConversationListViewModel : ObservableObject
{
    private readonly IStorageService _storageService;
    private readonly ILogger<ConversationListViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<ConversationInfo> _conversations = new();

    [ObservableProperty]
    private ObservableCollection<ConversationGroup> _groupedConversations = new();

    [ObservableProperty]
    private ConversationInfo? _selectedConversation;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    public event EventHandler<Conversation>? ConversationSelected;
    public event EventHandler? ConversationDeleted;

    public ConversationListViewModel(
        IStorageService storageService,
        ILogger<ConversationListViewModel> logger)
    {
        _storageService = storageService;
        _logger = logger;

        _ = LoadConversationsAsync();
    }

    [RelayCommand]
    private async Task LoadConversationsAsync()
    {
        IsLoading = true;
        try
        {
            var files = await _storageService.GetAllConversationFilesAsync();
            var conversationInfos = new List<ConversationInfo>();

            foreach (var file in files)
            {
                try
                {
                    var conversation = await _storageService.LoadConversationFromFileAsync(file);
                    if (conversation != null)
                    {
                        conversationInfos.Add(new ConversationInfo
                        {
                            Id = conversation.Id,
                            Title = conversation.Title,
                            CreatedAt = conversation.CreatedAt,
                            UpdatedAt = conversation.UpdatedAt,
                            MessageCount = conversation.Messages.Count,
                            Provider = conversation.Metadata.Provider,
                            ModelName = conversation.Metadata.ModelName,
                            FilePath = file,
                            SearchableText = BuildSearchableText(conversation)
                        });
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load conversation from {File}", file);
                }
            }

            // Sort by update time (most recent first)
            conversationInfos = conversationInfos.OrderByDescending(c => c.UpdatedAt).ToList();

            Conversations.Clear();
            foreach (var info in conversationInfos)
            {
                Conversations.Add(info);
            }

            GroupConversations();

            _logger.LogInformation("Loaded {Count} conversations", Conversations.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load conversations");
            MessageBox.Show($"Failed to load conversations: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SelectConversationAsync(ConversationInfo? info)
    {
        if (info == null || IsLoading)
            return;

        try
        {
            var conversation = await _storageService.LoadConversationAsync(info.Id);
            if (conversation != null)
            {
                SelectedConversation = info;
                ConversationSelected?.Invoke(this, conversation);
                _logger.LogInformation("Selected conversation: {Title}", info.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load conversation {Id}", info.Id);
            MessageBox.Show($"Failed to load conversation: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteConversationAsync(ConversationInfo? info)
    {
        if (info == null)
            return;

        var result = MessageBox.Show(
            $"Are you sure you want to delete '{info.Title}'?",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                await _storageService.DeleteConversationAsync(info.Id);
                Conversations.Remove(info);
                GroupConversations();
                _logger.LogInformation("Deleted conversation: {Title}", info.Title);

                // Notify that a conversation was deleted so the main window can create a new empty one
                ConversationDeleted?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete conversation {Id}", info.Id);
                MessageBox.Show($"Failed to delete conversation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    [RelayCommand]
    private async Task RenameConversationAsync(ConversationInfo? info)
    {
        if (info == null)
            return;

        var dialog = new Views.RenameConversationDialog(info.Title)
        {
            Owner = Application.Current.MainWindow
        };

        if (dialog.ShowDialog() == true && dialog.NewTitle != null)
        {
            var newTitle = dialog.NewTitle;

            if (newTitle == info.Title)
                return;

            try
            {
                // Load the conversation, update the title, and save it back
                var conversation = await _storageService.LoadConversationAsync(info.Id);
                if (conversation != null)
                {
                    conversation.Title = newTitle;
                    conversation.UpdatedAt = DateTime.UtcNow;
                    await _storageService.SaveConversationAsync(conversation);

                    // Update the local info
                    info.Title = newTitle;
                    info.UpdatedAt = conversation.UpdatedAt;

                    // Re-group to update the display
                    if (string.IsNullOrWhiteSpace(SearchText))
                    {
                        GroupConversations();
                    }
                    else
                    {
                        FilterConversations();
                    }

                    _logger.LogInformation("Renamed conversation to: {Title}", newTitle);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to rename conversation {Id}", info.Id);
                MessageBox.Show($"Failed to rename conversation: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        FilterConversations();
    }

    private void FilterConversations()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var conversation in Conversations)
                conversation.MatchSnippet = string.Empty;

            GroupConversations();
            return;
        }

        var term = SearchText.Trim();
        var lowered = term.ToLowerInvariant();
        var matches = new List<ConversationInfo>();

        foreach (var conversation in Conversations)
        {
            var inTitle = conversation.Title.Contains(term, StringComparison.OrdinalIgnoreCase);
            var bodyIndex = conversation.SearchableText.IndexOf(lowered, StringComparison.Ordinal);

            if (!inTitle && bodyIndex < 0)
                continue;

            // Only show a snippet for body hits; a title hit is already visible.
            conversation.MatchSnippet = bodyIndex >= 0
                ? BuildSnippet(conversation.SearchableText, bodyIndex, lowered.Length)
                : string.Empty;

            matches.Add(conversation);
        }

        GroupedConversations.Clear();
        GroupedConversations.Add(new ConversationGroup
        {
            Name = matches.Count == 1 ? "1 result" : $"{matches.Count} results",
            Conversations = new ObservableCollection<ConversationInfo>(matches)
        });
    }

    /// <summary>
    /// Flattens every message into one lower-cased blob. Done once per load so
    /// that filtering on each keystroke does not re-walk the message objects.
    /// </summary>
    private static string BuildSearchableText(Conversation conversation)
    {
        var sb = new StringBuilder();

        foreach (var message in conversation.Messages)
        {
            sb.Append(message.Content);
            sb.Append('\n');
        }

        return sb.ToString().ToLowerInvariant();
    }

    /// <summary>Extracts readable context around a hit, on word boundaries.</summary>
    private static string BuildSnippet(string text, int index, int termLength)
    {
        const int contextBefore = 40;
        const int contextAfter = 90;

        var start = Math.Max(0, index - contextBefore);
        var end = Math.Min(text.Length, index + termLength + contextAfter);

        // Avoid slicing mid-word at either edge.
        if (start > 0)
        {
            var space = text.IndexOf(' ', start, Math.Min(15, index - start));
            if (space > 0) start = space + 1;
        }

        var snippet = text[start..end].Replace('\n', ' ').Trim();

        if (start > 0) snippet = "..." + snippet;
        if (end < text.Length) snippet += "...";

        return snippet;
    }

    private void GroupConversations()
    {
        GroupedConversations.Clear();

        var now = DateTime.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);
        var monthAgo = today.AddMonths(-1);

        var groups = new List<ConversationGroup>
        {
            new() { Name = "Today", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "Yesterday", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "This Week", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "This Month", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "Older", Conversations = new ObservableCollection<ConversationInfo>() }
        };

        foreach (var conv in Conversations)
        {
            var date = conv.UpdatedAt.ToLocalTime().Date;

            if (date == today)
                groups[0].Conversations.Add(conv);
            else if (date == yesterday)
                groups[1].Conversations.Add(conv);
            else if (date > weekAgo)
                groups[2].Conversations.Add(conv);
            else if (date > monthAgo)
                groups[3].Conversations.Add(conv);
            else
                groups[4].Conversations.Add(conv);
        }

        foreach (var group in groups.Where(g => g.Conversations.Count > 0))
        {
            GroupedConversations.Add(group);
        }
    }
}

public partial class ConversationInfo : ObservableObject
{
    private string _matchSnippet = string.Empty;
    private string _title = string.Empty;

    public Guid Id { get; set; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// All message text, flattened and lower-cased once at load time so
    /// filtering on each keystroke stays a simple substring scan.
    /// </summary>
    public string SearchableText { get; set; } = string.Empty;

    /// <summary>
    /// Text around the search hit, shown under the title so a match inside a
    /// long conversation is visible without opening it. Empty when not searching.
    /// </summary>
    public string MatchSnippet
    {
        get => _matchSnippet;
        set => SetProperty(ref _matchSnippet, value);
    }

    public bool HasMatchSnippet => !string.IsNullOrEmpty(MatchSnippet);

    public string UpdatedAtDisplay =>
        UpdatedAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");

    public string Summary =>
        $"{MessageCount} messages • {Provider}";
}

public class ConversationGroup
{
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<ConversationInfo> Conversations { get; set; } = new();
}
