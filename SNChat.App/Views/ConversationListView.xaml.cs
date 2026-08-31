using System.Windows.Controls;
using SNChat.App.ViewModels;

namespace SNChat.App.Views;

public partial class ConversationListView : UserControl
{
    public event EventHandler? NewConversationRequested;

    public ConversationListView()
    {
        InitializeComponent();

        // Wire up the New Conversation button
        NewConversationButton.Click += (s, e) => NewConversationRequested?.Invoke(this, EventArgs.Empty);
    }

    public ConversationListView(ConversationListViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    /// <summary>Puts the caret in the search box and selects any existing term.</summary>
    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }
}
