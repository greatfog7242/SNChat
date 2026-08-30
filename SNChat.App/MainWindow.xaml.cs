using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SNChat.App.ViewModels;
using SNChat.App.Views;

namespace SNChat.App;

public partial class MainWindow : Window
{
    private readonly ChatViewModel _chatViewModel;
    private readonly ConversationListViewModel _conversationListViewModel;
    private readonly IServiceProvider _serviceProvider;

    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;

        // Create ViewModels
        _chatViewModel = serviceProvider.GetRequiredService<ChatViewModel>();
        _conversationListViewModel = serviceProvider.GetRequiredService<ConversationListViewModel>();

        // Create ChatView with service provider for settings
        var chatView = new ChatView(_chatViewModel, serviceProvider);
        ((System.Windows.Controls.Grid)Content).Children.Remove(ChatViewControl);
        System.Windows.Controls.Grid.SetColumn(chatView, 2);
        ((System.Windows.Controls.Grid)Content).Children.Add(chatView);

        // Set DataContext for conversation list
        ConversationListControl.DataContext = _conversationListViewModel;

        // Wire up conversation selection
        _conversationListViewModel.ConversationSelected += OnConversationSelected;
        _chatViewModel.ConversationSaved += OnConversationSaved;
    }

    private void OnConversationSelected(object? sender, Core.Models.Conversation conversation)
    {
        _chatViewModel.LoadConversation(conversation);
    }

    private void OnConversationSaved(object? sender, EventArgs e)
    {
        // Refresh conversation list when a conversation is saved
        _conversationListViewModel.LoadConversationsCommand.Execute(null);
    }
}