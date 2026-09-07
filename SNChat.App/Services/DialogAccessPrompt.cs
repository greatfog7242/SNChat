using System.Windows;
using SNChat.Core.Services;

namespace SNChat.App.Services;

/// <summary>
/// Asks the user, in a dialog, whether the assistant may read a folder outside
/// the ones it was given.
///
/// The wording names the folder rather than only the file, because the folder is
/// what is actually being granted. A dialog saying "may I open notes.txt" that
/// quietly hands over the whole directory would be the sort of consent that is
/// technically obtained and practically meaningless.
/// </summary>
public class DialogAccessPrompt : IAccessPrompt
{
    public Task<bool> RequestAccessAsync(
        string folder,
        string requestedPath,
        string toolName,
        CancellationToken cancellationToken = default)
    {
        var application = Application.Current;

        // No window means nobody to ask, and silence is not consent.
        if (application?.Dispatcher == null)
            return Task.FromResult(false);

        var message =
            $"The assistant is trying to open:{Environment.NewLine}" +
            $"    {requestedPath}{Environment.NewLine}{Environment.NewLine}" +
            $"That is outside the folders it is allowed to read. Allowing it means " +
            $"giving it access to this folder and everything in it:" +
            $"{Environment.NewLine}    {folder}{Environment.NewLine}{Environment.NewLine}" +
            "It will be able to read and change files there until you close SNChat. " +
            "Nothing is remembered after that." +
            $"{Environment.NewLine}{Environment.NewLine}Allow it?";

        return application.Dispatcher.InvokeAsync(() =>
        {
            var answer = MessageBox.Show(
                application.MainWindow!,
                message,
                "Allow access to this folder?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                // Defaults to No, so that dismissing the dialog with Escape or
                // Enter does not grant anything by accident.
                MessageBoxResult.No);

            return answer == MessageBoxResult.Yes;
        }).Task;
    }
}
