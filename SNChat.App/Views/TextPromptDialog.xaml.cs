using System.Windows;

namespace SNChat.App.Views;

/// <summary>
/// Asks for a single line of text. Used for naming a group and for retitling a
/// conversation, which differ only in their wording.
/// </summary>
public partial class TextPromptDialog : Window
{
    private TextPromptDialog(string title, string label, string initialValue)
    {
        InitializeComponent();

        Title = title;
        PromptLabel.Text = label;
        ValueTextBox.Text = initialValue;

        // Focus has to wait for the window to be up; setting it in the
        // constructor lands on a control that is not shown yet and is lost.
        Loaded += (_, _) =>
        {
            ValueTextBox.Focus();
            ValueTextBox.SelectAll();
        };
    }

    /// <summary>
    /// Shows the prompt over the main window. Returns the trimmed text, or null
    /// if the user cancelled.
    /// </summary>
    public static string? Prompt(string title, string label, string initialValue = "")
    {
        var dialog = new TextPromptDialog(title, label, initialValue)
        {
            Owner = Application.Current.MainWindow
        };

        return dialog.ShowDialog() == true ? dialog.Value : null;
    }

    private string? Value { get; set; }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var value = ValueTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            MessageBox.Show(this, "The name cannot be empty.", "Invalid Name",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Value = value;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
