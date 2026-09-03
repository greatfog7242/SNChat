using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SNChat.App.Views;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;

namespace SNChat.App.ViewModels;

public partial class ConversationListViewModel : ObservableObject
{
    private readonly IStorageService _storageService;
    private readonly IGroupService _groupService;
    private readonly ILogger<ConversationListViewModel> _logger;

    /// <summary>
    /// Where a shift-click measures its range from: the last conversation
    /// picked without shift held.
    /// </summary>
    private ConversationInfo? _selectionAnchor;

    [ObservableProperty]
    private ObservableCollection<ConversationInfo> _conversations = new();

    /// <summary>The user's own groups, in the order they were made.</summary>
    [ObservableProperty]
    private ObservableCollection<ConversationGroupViewModel> _groups = new();

    /// <summary>
    /// Conversations in no group, in the usual date sections. While a search is
    /// running this holds the flat list of hits instead.
    /// </summary>
    [ObservableProperty]
    private ObservableCollection<ConversationDateGroup> _groupedConversations = new();

    [ObservableProperty]
    private ConversationInfo? _selectedConversation;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _isLoading;

    /// <summary>
    /// Groups are hidden while searching: a search looks across every
    /// conversation, so splitting the hits by group would bury them.
    /// </summary>
    [ObservableProperty]
    private bool _showGroups = true;

    /// <summary>
    /// True while a drag hovers the ungrouped area, which is how a conversation
    /// is taken back out of a group.
    /// </summary>
    [ObservableProperty]
    private bool _isUngroupedDropTarget;

    public event EventHandler<Conversation>? ConversationSelected;
    public event EventHandler? ConversationDeleted;

    public ConversationListViewModel(
        IStorageService storageService,
        IGroupService groupService,
        ILogger<ConversationListViewModel> logger)
    {
        _storageService = storageService;
        _groupService = groupService;
        _logger = logger;

        _ = LoadConversationsAsync();
    }

    /// <summary>
    /// Every conversation the sidebar is currently showing, top to bottom.
    /// Shift-click needs this because a range can run from a group into the
    /// ungrouped list below it.
    /// </summary>
    public IEnumerable<ConversationInfo> VisibleOrder
    {
        get
        {
            if (ShowGroups)
            {
                foreach (var group in Groups)
                {
                    if (!group.IsExpanded)
                        continue;

                    foreach (var conversation in group.Conversations)
                        yield return conversation;
                }
            }

            foreach (var section in GroupedConversations)
            {
                foreach (var conversation in section.Conversations)
                    yield return conversation;
            }
        }
    }

    public IReadOnlyList<ConversationInfo> SelectedConversations =>
        Conversations.Where(c => c.IsSelected).ToList();

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

            // Conversations deleted while the app was closed would otherwise
            // keep padding the counts on their group headers.
            await _groupService.PruneAsync(Conversations.Select(c => c.Id));

            await RebuildGroupingAsync();

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
                await _groupService.UnassignAsync(new[] { info.Id });

                Conversations.Remove(info);
                await RebuildGroupingAsync();
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

        var newTitle = TextPromptDialog.Prompt(
            "Rename Conversation", "Enter new conversation title:", info.Title);

        if (newTitle == null || newTitle == info.Title)
            return;

        try
        {
            var conversation = await _storageService.LoadConversationAsync(info.Id);
            if (conversation == null)
                return;

            conversation.Title = newTitle;
            await _storageService.SaveConversationAsync(conversation);

            info.Title = newTitle;
            info.UpdatedAt = conversation.UpdatedAt;

            _logger.LogInformation("Renamed conversation to: {Title}", newTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename conversation {Id}", info.Id);
            MessageBox.Show($"Failed to rename conversation: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task NewGroupAsync()
    {
        var name = TextPromptDialog.Prompt("New Group", "Name the group:", "New Group");
        if (name == null)
            return;

        try
        {
            await _groupService.CreateGroupAsync(name);
            await RebuildGroupingAsync();
            _logger.LogInformation("Created group: {Name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create group {Name}", name);
            MessageBox.Show($"Failed to create group: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RenameGroupAsync(ConversationGroupViewModel? group)
    {
        if (group == null)
            return;

        var name = TextPromptDialog.Prompt("Rename Group", "Enter new group name:", group.Name);
        if (name == null || name == group.Name)
            return;

        try
        {
            await _groupService.RenameGroupAsync(group.Id, name);
            group.Name = name;
            _logger.LogInformation("Renamed group to: {Name}", name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename group {Id}", group.Id);
            MessageBox.Show($"Failed to rename group: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteGroupAsync(ConversationGroupViewModel? group)
    {
        if (group == null)
            return;

        var message = group.IsEmpty
            ? $"Delete the group '{group.Name}'?"
            : $"Delete the group '{group.Name}'?\n\n" +
              $"The {group.Count} conversation(s) in it are kept, and move back " +
              "to the ungrouped list.";

        var result = MessageBox.Show(message, "Confirm Delete",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            await _groupService.DeleteGroupAsync(group.Id);
            await RebuildGroupingAsync();
            _logger.LogInformation("Deleted group: {Name}", group.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete group {Id}", group.Id);
            MessageBox.Show($"Failed to delete group: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ToggleGroupAsync(ConversationGroupViewModel? group)
    {
        if (group == null)
            return;

        group.IsExpanded = !group.IsExpanded;
        OnPropertyChanged(nameof(VisibleOrder));

        try
        {
            await _groupService.SetExpandedAsync(group.Id, group.IsExpanded);
        }
        catch (Exception ex)
        {
            // Folding is a view preference; failing to remember it is not worth
            // interrupting the user for.
            _logger.LogWarning(ex, "Failed to remember group {Id} fold state", group.Id);
        }
    }

    [RelayCommand]
    private async Task RemoveFromGroupAsync(ConversationInfo? info)
    {
        if (info == null)
            return;

        // A right-click on one of several selected conversations acts on all of
        // them, matching what a drag would have done.
        var ids = info.IsSelected
            ? SelectedConversations.Select(c => c.Id).ToList()
            : new List<Guid> { info.Id };

        await RemoveFromGroupAsync(ids);
    }

    /// <summary>Files conversations under a group. Called by a drop.</summary>
    public async Task MoveToGroupAsync(IReadOnlyList<Guid> conversationIds, Guid groupId)
    {
        if (conversationIds.Count == 0)
            return;

        try
        {
            await _groupService.AssignAsync(conversationIds, groupId);
            await RebuildGroupingAsync();
            _logger.LogInformation("Moved {Count} conversation(s) into group {Id}",
                conversationIds.Count, groupId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move conversations into group {Id}", groupId);
            MessageBox.Show($"Failed to move conversations: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Takes conversations out of whatever group holds them.</summary>
    public async Task RemoveFromGroupAsync(IReadOnlyList<Guid> conversationIds)
    {
        if (conversationIds.Count == 0)
            return;

        try
        {
            await _groupService.UnassignAsync(conversationIds);
            await RebuildGroupingAsync();
            _logger.LogInformation("Removed {Count} conversation(s) from their group",
                conversationIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove conversations from their group");
            MessageBox.Show($"Failed to remove conversations: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Picks one conversation and drops any other selection.</summary>
    public void SelectOnly(ConversationInfo info)
    {
        foreach (var conversation in Conversations)
            conversation.IsSelected = ReferenceEquals(conversation, info);

        _selectionAnchor = info;
    }

    /// <summary>Adds or removes one conversation from the selection.</summary>
    public void ToggleSelection(ConversationInfo info)
    {
        info.IsSelected = !info.IsSelected;
        _selectionAnchor = info;
    }

    /// <summary>Selects everything between the anchor and this conversation.</summary>
    public void SelectRangeTo(ConversationInfo info)
    {
        if (_selectionAnchor == null)
        {
            SelectOnly(info);
            return;
        }

        var order = VisibleOrder.ToList();
        var from = order.IndexOf(_selectionAnchor);
        var to = order.IndexOf(info);

        if (from < 0 || to < 0)
        {
            SelectOnly(info);
            return;
        }

        if (from > to)
            (from, to) = (to, from);

        foreach (var conversation in Conversations)
            conversation.IsSelected = false;

        for (var i = from; i <= to; i++)
            order[i].IsSelected = true;
    }

    public void ClearSelection()
    {
        foreach (var conversation in Conversations)
            conversation.IsSelected = false;

        _selectionAnchor = null;
    }

    partial void OnSearchTextChanged(string value)
    {
        _ = FilterConversationsAsync();
    }

    private async Task FilterConversationsAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            foreach (var conversation in Conversations)
                conversation.MatchSnippet = string.Empty;

            ShowGroups = true;
            await RebuildGroupingAsync();
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

        // A search runs across everything, grouped or not, so the groups fold
        // away and the hits are listed flat.
        ShowGroups = false;
        Groups.Clear();

        GroupedConversations.Clear();
        GroupedConversations.Add(new ConversationDateGroup
        {
            Name = matches.Count == 1 ? "1 result" : $"{matches.Count} results",
            Conversations = new ObservableCollection<ConversationInfo>(matches)
        });

        OnPropertyChanged(nameof(VisibleOrder));
    }

    /// <summary>
    /// Splits the loaded conversations into the user's groups and the dated
    /// remainder. Reads the groups back from the service each time so that the
    /// file on disk stays the one source of truth.
    /// </summary>
    private async Task RebuildGroupingAsync()
    {
        if (!ShowGroups)
            return;

        var stored = await _groupService.GetGroupsAsync();
        var byId = Conversations.ToDictionary(c => c.Id);

        Groups.Clear();
        var grouped = new HashSet<Guid>();

        foreach (var definition in stored)
        {
            var group = new ConversationGroupViewModel(
                definition.Id, definition.Name, definition.IsExpanded);

            foreach (var id in definition.ConversationIds)
            {
                if (!byId.TryGetValue(id, out var info))
                    continue;

                group.Conversations.Add(info);
                grouped.Add(id);
            }

            Groups.Add(group);
        }

        GroupUngroupedByDate(grouped);
        OnPropertyChanged(nameof(VisibleOrder));
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

    private void GroupUngroupedByDate(HashSet<Guid> grouped)
    {
        GroupedConversations.Clear();

        var now = DateTime.Now;
        var today = now.Date;
        var yesterday = today.AddDays(-1);
        var weekAgo = today.AddDays(-7);
        var monthAgo = today.AddMonths(-1);

        var groups = new List<ConversationDateGroup>
        {
            new() { Name = "Today", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "Yesterday", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "This Week", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "This Month", Conversations = new ObservableCollection<ConversationInfo>() },
            new() { Name = "Older", Conversations = new ObservableCollection<ConversationInfo>() }
        };

        foreach (var conv in Conversations)
        {
            if (grouped.Contains(conv.Id))
                continue;

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
    private bool _isSelected;
    private DateTime _updatedAt;

    public Guid Id { get; set; }

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (SetProperty(ref _updatedAt, value))
                OnPropertyChanged(nameof(UpdatedAtDisplay));
        }
    }

    public int MessageCount { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Part of the current multi-selection, which is what a drag carries into a
    /// group. Distinct from the conversation being open in the chat pane.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

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
        set
        {
            if (SetProperty(ref _matchSnippet, value))
                OnPropertyChanged(nameof(HasMatchSnippet));
        }
    }

    public bool HasMatchSnippet => !string.IsNullOrEmpty(MatchSnippet);

    public string UpdatedAtDisplay =>
        UpdatedAt.ToLocalTime().ToString("MMM dd, yyyy HH:mm");

    public string Summary =>
        $"{MessageCount} messages • {Provider}";
}

/// <summary>One of the Today / Yesterday / This Week sections.</summary>
public class ConversationDateGroup
{
    public string Name { get; set; } = string.Empty;
    public ObservableCollection<ConversationInfo> Conversations { get; set; } = new();
}
