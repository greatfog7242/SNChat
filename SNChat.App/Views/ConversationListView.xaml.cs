using System.Windows.Controls;
using SNChat.App.ViewModels;

namespace SNChat.App.Views;

public partial class ConversationListView : UserControl
{
    public ConversationListView()
    {
        InitializeComponent();
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
