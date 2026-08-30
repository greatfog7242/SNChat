using System.Collections.ObjectModel;
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
                            FilePath = file
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to delete conversation {Id}", info.Id);
                MessageBox.Show($"Failed to delete conversation: {ex.Message}", "Error",
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
            GroupConversations();
            return;
        }

        var filtered = Conversations
            .Where(c => c.Title.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            .ToList();

        GroupedConversations.Clear();
        var group = new ConversationGroup { Name = "Search Results", Conversations = new ObservableCollection<ConversationInfo>(filtered) };
        GroupedConversations.Add(group);
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

public class ConversationInfo
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

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
