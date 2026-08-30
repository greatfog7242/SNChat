using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using SNChat.App.ViewModels;
using SNChat.App.Views;

namespace SNChat.App;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider serviceProvider)
    {
        InitializeComponent();

        // Create ChatView with injected ViewModel
        var chatViewModel = serviceProvider.GetRequiredService<ChatViewModel>();
        var chatView = new ChatView(chatViewModel);

        // Replace the placeholder ChatView in XAML with the DI-created one
        ChatViewControl.DataContext = chatViewModel;
    }
}