using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using SNChat.App.ViewModels;
using SNChat.App.Views;

namespace SNChat.App;

public partial class MainWindow : Window
{
    private readonly ChatViewModel _chatViewModel;
    private readonly ConversationListViewModel _conversationListViewModel;
    private readonly IServiceProvider _serviceProvider;
    private readonly ChatView _chatView;

    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _serviceProvider = serviceProvider;

        // Create ViewModels
        _chatViewModel = serviceProvider.GetRequiredService<ChatViewModel>();
        _conversationListViewModel = serviceProvider.GetRequiredService<ConversationListViewModel>();

        // Create ChatView with service provider for settings
        _chatView = new ChatView(_chatViewModel, serviceProvider);
        ((System.Windows.Controls.Grid)Content).Children.Remove(ChatViewControl);
        System.Windows.Controls.Grid.SetColumn(_chatView, 2);
        ((System.Windows.Controls.Grid)Content).Children.Add(_chatView);

        // Set DataContext for conversation list
        ConversationListControl.DataContext = _conversationListViewModel;

        // Wire up conversation selection
        _conversationListViewModel.ConversationSelected += OnConversationSelected;
        _chatViewModel.ConversationSaved += OnConversationSaved;

        RegisterShortcuts();
    }

    /// <summary>
    /// Bound in code rather than XAML because the window itself has no view
    /// model; each pane carries its own DataContext.
    /// </summary>
    private void RegisterShortcuts()
    {
        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => _chatViewModel.NewConversationCommand.Execute(null)),
            Key.N, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(
            new RelayCommand(FocusConversationSearch),
            Key.F, ModifierKeys.Control));

        InputBindings.Add(new KeyBinding(
            new RelayCommand(() => _chatView?.ShowTemplatePicker()),
            Key.T, ModifierKeys.Control));
    }

    private void FocusConversationSearch()
    {
        // The control is found by name because the chat pane is swapped at
        // construction, leaving the sidebar as the stable reference.
        if (ConversationListControl is Views.ConversationListView list)
            list.FocusSearch();
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