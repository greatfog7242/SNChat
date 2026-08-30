using System.Windows;
using SNChat.App.ViewModels;

namespace SNChat.App.Views;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        Loaded += SettingsWindow_Loaded;
    }

    private void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Set PasswordBox values (PasswordBox doesn't support binding)
        FreeTokenApiKeyBox.Password = _viewModel.FreeTokenApiKey;
        OpenRouterApiKeyBox.Password = _viewModel.OpenRouterApiKey;
        AnthropicApiKeyBox.Password = _viewModel.AnthropicApiKey;
        OpenAIApiKeyBox.Password = _viewModel.OpenAIApiKey;
        GoogleApiKeyBox.Password = _viewModel.GoogleApiKey;

        // Wire up PasswordChanged events
        FreeTokenApiKeyBox.PasswordChanged += (s, args) =>
            _viewModel.FreeTokenApiKey = FreeTokenApiKeyBox.Password;

        OpenRouterApiKeyBox.PasswordChanged += (s, args) =>
            _viewModel.OpenRouterApiKey = OpenRouterApiKeyBox.Password;

        AnthropicApiKeyBox.PasswordChanged += (s, args) =>
            _viewModel.AnthropicApiKey = AnthropicApiKeyBox.Password;

        OpenAIApiKeyBox.PasswordChanged += (s, args) =>
            _viewModel.OpenAIApiKey = OpenAIApiKeyBox.Password;

        GoogleApiKeyBox.PasswordChanged += (s, args) =>
            _viewModel.GoogleApiKey = GoogleApiKeyBox.Password;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.HasUnsavedChanges)
        {
            var result = MessageBox.Show(
                "You have unsaved changes. Are you sure you want to close?",
                "Unsaved Changes",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
                return;
        }

        Close();
    }
}
