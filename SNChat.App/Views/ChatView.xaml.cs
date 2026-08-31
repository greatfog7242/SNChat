using System.Diagnostics;
using System.IO;
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

    /// <summary>
    /// Enter sends; Shift+Enter inserts a newline. Handled here rather than with
    /// a KeyBinding because the binding fires for both and would block newlines.
    /// </summary>
    private void MessageInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Return && e.Key != Key.Enter)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
            return; // let the TextBox insert the newline itself

        if (DataContext is ChatViewModel chat && chat.SendMessageCommand.CanExecute(null))
            chat.SendMessageCommand.Execute(null);

        e.Handled = true;
    }

    private void ChatView_DragOver(object sender, DragEventArgs e)
    {
        var isFileDrop = e.Data.GetDataPresent(DataFormats.FileDrop);

        e.Effects = isFileDrop ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlay.Visibility = isFileDrop ? Visibility.Visible : Visibility.Collapsed;

        // Without this the drop is refused, since the default handling of the
        // tunnelling event rejects unrecognised data.
        e.Handled = true;
    }

    private void ChatView_DragLeave(object sender, DragEventArgs e) =>
        DropOverlay.Visibility = Visibility.Collapsed;

    private async void ChatView_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;

        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            DataContext is not ChatViewModel chat)
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
            return;

        // Dropping a folder is an easy mistake; take the files inside it.
        var files = new List<string>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path));
            else
                files.Add(path);
        }

        await chat.AddAttachmentsAsync(files);
        MessageInput.Focus();
    }

    private void TemplatesButton_Click(object sender, RoutedEventArgs e) => ShowTemplatePicker();

    /// <summary>Opens the picker and drops the filled-in prompt into the input box.</summary>
    public void ShowTemplatePicker()
    {
        if (_serviceProvider == null || DataContext is not ChatViewModel chat)
            return;

        var pickerViewModel = _serviceProvider.GetRequiredService<TemplatePickerViewModel>();
        var window = new TemplatePickerWindow(pickerViewModel)
        {
            Owner = Window.GetWindow(this)
        };

        if (window.ShowDialog() == true && window.Result != null)
        {
            chat.ApplyTemplate(
                window.Result.Prompt,
                window.Result.SystemPrompt,
                window.Result.TemplateName);

            MessageInput.Focus();
            MessageInput.CaretIndex = MessageInput.Text.Length;
        }
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
