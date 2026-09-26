using System.Windows;
using SNChat.Core.Services;

namespace SNChat.App.Services;

/// <summary>
/// Asks the user, in a dialog, to agree to one act that changes the machine.
///
/// The detail line carries the target - which process, which service - and is
/// shown on its own rather than folded into the sentence. A dialog that says
/// "the assistant wants to stop a service" is one a user can only answer by
/// guessing; naming it is the difference between consent and a reflex.
/// </summary>
public class DialogActionPrompt : IActionPrompt
{
    public Task<bool> ConfirmAsync(
        string summary,
        string detail,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        var application = Application.Current;

        // No window means nobody to ask, and silence is not consent.
        if (application?.Dispatcher == null)
            return Task.FromResult(false);

        var message =
            $"The assistant wants to do this on your computer:{Environment.NewLine}" +
            $"    {detail}{Environment.NewLine}{Environment.NewLine}" +
            $"This changes the machine and cannot be undone from here." +
            $"{Environment.NewLine}{Environment.NewLine}Go ahead?";

        return application.Dispatcher.InvokeAsync(() =>
        {
            var answer = MessageBox.Show(
                application.MainWindow!,
                message,
                summary,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                // Defaults to No, so that dismissing the dialog with Escape or
                // Enter does not agree to anything by accident.
                MessageBoxResult.No);

            return answer == MessageBoxResult.Yes;
        }).Task;
    }
}
