using System.Windows;

namespace SNChat.App.Views;

public partial class RenameConversationDialog : Window
{
    public string? NewTitle { get; private set; }

    public RenameConversationDialog(string currentTitle)
    {
        InitializeComponent();

        TitleTextBox.Text = currentTitle;
        TitleTextBox.Focus();
        TitleTextBox.SelectAll();

        Loaded += (s, e) =>
        {
            TitleTextBox.Focus();
            TitleTextBox.SelectAll();
        };
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var newTitle = TitleTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newTitle))
        {
            MessageBox.Show("Title cannot be empty.", "Invalid Title",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        NewTitle = newTitle;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
