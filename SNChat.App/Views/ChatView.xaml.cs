using System.Windows.Controls;
using SNChat.App.ViewModels;

namespace SNChat.App.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
    }

    public ChatView(ChatViewModel viewModel) : this()
    {
        DataContext = viewModel;

        // Auto-scroll to bottom when new messages arrive
        viewModel.Messages.CollectionChanged += (s, e) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                MessageScrollViewer.ScrollToBottom();
            });
        };
    }
}
