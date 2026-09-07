using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SNChat.App.ViewModels;

/// <summary>
/// One user-made group as the sidebar shows it: a header that can be folded
/// away, and the conversations filed under it.
/// </summary>
public partial class ConversationGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _isExpanded = true;

    /// <summary>
    /// True while a drag hovers this header, so it can light up as the place
    /// the conversations would land.
    /// </summary>
    [ObservableProperty]
    private bool _isDropTarget;

    /// <summary>
    /// True while another group is dragged over this one's top half, marking
    /// where it would be dropped in the order.
    /// </summary>
    [ObservableProperty]
    private bool _isReorderAbove;

    /// <summary>As <see cref="IsReorderAbove"/>, for the bottom half.</summary>
    [ObservableProperty]
    private bool _isReorderBelow;

    public ConversationGroupViewModel(Guid id, string name, bool isExpanded)
    {
        Id = id;
        _name = name;
        _isExpanded = isExpanded;

        Conversations.CollectionChanged += OnConversationsChanged;
    }

    public Guid Id { get; }

    public ObservableCollection<ConversationInfo> Conversations { get; } = new();

    public int Count => Conversations.Count;

    /// <summary>Shown on the header so a folded group still says how much it holds.</summary>
    public string CountDisplay => Conversations.Count.ToString();

    public bool IsEmpty => Conversations.Count == 0;

    private void OnConversationsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(CountDisplay));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
