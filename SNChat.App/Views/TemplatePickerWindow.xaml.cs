using System.Windows;
using SNChat.App.ViewModels;

namespace SNChat.App.Views;

public partial class TemplatePickerWindow : Window
{
    /// <summary>The confirmed prompt, or null when the user cancelled.</summary>
    public TemplateResult? Result { get; private set; }

    public TemplatePickerWindow(TemplatePickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        viewModel.TemplateAccepted += (_, result) =>
        {
            Result = result;
            DialogResult = true;
        };
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
