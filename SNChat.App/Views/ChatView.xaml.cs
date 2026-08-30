using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Extensions.DependencyInjection;
using SNChat.App.ViewModels;

namespace SNChat.App.Views;

public partial class ChatView : UserControl
{
    private readonly IServiceProvider? _serviceProvider;

    public ChatView()
    {
        InitializeComponent();

        // Markdig renders markdown links as Hyperlinks, but WPF does nothing on
        // click without a navigation handler, so source links would be dead.
        AddHandler(Hyperlink.RequestNavigateEvent,
            new RequestNavigateEventHandler(OnRequestNavigate));
    }

    private void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        var uri = e.Uri;

        // Links here originate from model output, so only ordinary web URLs are
        // followed. Anything else (file://, custom schemes) is refused rather
        // than handed to the shell.
        if (uri is null ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(
                $"Refused to open a non-web link: {uri}",
                "Blocked link",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Handled = true;
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open the link: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        e.Handled = true;
    }

    public ChatView(ChatViewModel viewModel, IServiceProvider serviceProvider) : this()
    {
        DataContext = viewModel;
        _serviceProvider = serviceProvider;

        // Auto-scroll to bottom when new messages arrive
        viewModel.Messages.CollectionChanged += (s, e) =>
        {
            Dispatcher.InvokeAsync(() =>
            {
                MessageScrollViewer.ScrollToBottom();
            });
        };
    }

    /// <summary>
    /// Every rendered message contains a MarkdownViewer, which wraps its own
    /// FlowDocumentScrollViewer. That inner scroller marks the bubbling wheel
    /// event handled, so the message list never receives it and the page appears
    /// frozen. Handling the tunnelling Preview event instead lets the outer list
    /// scroll first, which is what the wheel should do here.
    /// </summary>
    private void MessageScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        MessageScrollViewer.ScrollToVerticalOffset(
            MessageScrollViewer.VerticalOffset - e.Delta);

        e.Handled = true;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_serviceProvider == null)
            return;

        var settingsViewModel = _serviceProvider.GetRequiredService<SettingsViewModel>();
        var settingsWindow = new SettingsWindow(settingsViewModel)
        {
            Owner = Window.GetWindow(this)
        };

        settingsWindow.ShowDialog();
    }
}
