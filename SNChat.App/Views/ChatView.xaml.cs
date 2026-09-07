using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Navigation;
using System.Windows.Threading;
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

        viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    /// <summary>
    /// Puts the caret back in the input box once the turn is over.
    ///
    /// The box is disabled while a reply streams, and WPF drops keyboard focus
    /// from a control it disables without handing it back when it is re-enabled.
    /// Clicking Send loses it the same way, since the button is disabled too.
    /// Either way the caret ended up nowhere and the next message could not be
    /// typed until the box was clicked again.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // IsAgentRunning as well as IsStreaming: an automatic run drops
        // IsStreaming between its turns, and the box stays disabled across the
        // whole run, so only the end of the run is the moment to hand focus back.
        if (e.PropertyName != nameof(ChatViewModel.IsStreaming) &&
            e.PropertyName != nameof(ChatViewModel.IsAgentRunning))
        {
            return;
        }

        if (DataContext is not ChatViewModel chat || chat.IsStreaming || chat.IsAgentRunning)
            return;

        // Queued rather than called here: the IsEnabled binding has not run yet
        // at this point, and Focus() on a still-disabled TextBox is dropped.
        Dispatcher.InvokeAsync(RestoreInputFocus, DispatcherPriority.Input);
    }

    private void RestoreInputFocus()
    {
        // Only when the chat window is the one in front. A modal dialog - the
        // settings window, the template picker, an access prompt - deactivates
        // it, and pulling focus out from under that dialog would be worse than
        // leaving the caret where it is. IsEnabled is re-checked because a run
        // may have started again between the notification and this callback.
        if (Window.GetWindow(this)?.IsActive != true || !MessageInput.IsEnabled)
            return;

        MessageInput.Focus();
        MessageInput.CaretIndex = MessageInput.Text.Length;
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

    /// <summary>Opens the picker and drops the filled-in prompt into the input box.</summary>
    public void ShowTemplatePicker(Window? owner = null)
    {
        if (_serviceProvider == null || DataContext is not ChatViewModel chat)
            return;

        var pickerViewModel = _serviceProvider.GetRequiredService<TemplatePickerViewModel>();
        var window = new TemplatePickerWindow(pickerViewModel)
        {
            Owner = owner ?? Window.GetWindow(this)
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

        // The picker is owned by the settings window because that window is
        // modal; a picker owned by the main window would be blocked by it.
        settingsWindow.OpenTemplatesRequested += (s, args) => ShowTemplatePicker(settingsWindow);

        settingsWindow.ShowDialog();
    }
}
