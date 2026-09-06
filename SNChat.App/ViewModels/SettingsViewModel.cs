using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Providers.OpenRouter;

namespace SNChat.App.ViewModels;

/// <summary>One row in the OpenRouter model picker.</summary>
public partial class OpenRouterModelChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected;

    public string Id { get; init; } = string.Empty;
    public bool IsFree { get; init; }
    public long ContextWindow { get; init; }

    /// <summary>e.g. "free · 262k context" - shown beside the id.</summary>
    public string Detail =>
        $"{(IsFree ? "free" : "paid")} · {ContextWindow / 1000}k context";
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settingsService;
    private readonly OpenRouterProvider _openRouter;
    private readonly ILogger<SettingsViewModel> _logger;

    /// <summary>Every tool-capable model, kept so filtering does not refetch.</summary>
    private List<OpenRouterModelChoice> _allOpenRouterModels = new();

    [ObservableProperty]
    private ObservableCollection<OpenRouterModelChoice> _openRouterModels = new();

    [ObservableProperty]
    private string _openRouterModelFilter = string.Empty;

    [ObservableProperty]
    private bool _isLoadingOpenRouterModels;

    [ObservableProperty]
    private string _openRouterModelStatus = "Load the list to choose which models appear in the main window.";

    // Provider Settings
    [ObservableProperty]
    private string _ollamaBaseUrl = "http://localhost:11434";

    /// <summary>num_ctx for Ollama requests; 0 leaves the sizing to Ollama.</summary>
    [ObservableProperty]
    private int _ollamaContextWindow;

    [ObservableProperty]
    private string _freeTokenApiKey = string.Empty;

    [ObservableProperty]
    private string _freeTokenBaseUrl = "https://api.freetoken.ai/v1";

    [ObservableProperty]
    private string _openRouterApiKey = string.Empty;

    [ObservableProperty]
    private string _openRouterBaseUrl = "https://openrouter.ai/api/v1";

    [ObservableProperty]
    private string _anthropicApiKey = string.Empty;

    [ObservableProperty]
    private string _openAIApiKey = string.Empty;

    // Image search
    [ObservableProperty]
    private string _googleApiKey = string.Empty;

    [ObservableProperty]
    private string _googleSearchEngineId = string.Empty;

    [ObservableProperty]
    private string _imageSource = ImageSourcePreference.Auto;

    [ObservableProperty]
    private bool _fallbackToCommons = true;

    [ObservableProperty]
    private string _webSource = WebSourcePreference.Auto;

    [ObservableProperty]
    private bool _safeSearch = true;

    public IReadOnlyList<string> ImageSourceOptions => ImageSourcePreference.All;
    public IReadOnlyList<string> WebSourceOptions => WebSourcePreference.All;

    // Mode prompts, sent ahead of the conversation for whichever mode is picked
    // in the main window.
    [ObservableProperty]
    private string _chatModePrompt = string.Empty;

    [ObservableProperty]
    private string _codingModePrompt = string.Empty;

    [ObservableProperty]
    private string _scientificModePrompt = string.Empty;

    // Default Parameters
    [ObservableProperty]
    private double _defaultTemperature = 0.7;

    [ObservableProperty]
    private int _defaultMaxTokens = 2048;

    [ObservableProperty]
    private double _defaultTopP = 0.9;

    [ObservableProperty]
    private string _defaultProvider = "Ollama";

    [ObservableProperty]
    private string _defaultModel = string.Empty;

    // Context window: when to fold the older messages into a summary so the
    // history keeps fitting.
    [ObservableProperty]
    private bool _autoCompact = true;

    [ObservableProperty]
    private int _compactThresholdPercent = 80;

    [ObservableProperty]
    private int _keepRecentMessages = 6;

    [ObservableProperty]
    private int _fallbackWindowTokens = 8192;

    // UI Preferences
    [ObservableProperty]
    private string _theme = "Light";

    [ObservableProperty]
    private int _fontSize = 14;

    [ObservableProperty]
    private bool _showTimestamps = true;

    [ObservableProperty]
    private bool _enableMarkdown = true;

    [ObservableProperty]
    private int _sidebarWidth = 300;

    // Storage Settings
    [ObservableProperty]
    private string _conversationsPath = string.Empty;

    [ObservableProperty]
    private bool _autoSave = true;

    [ObservableProperty]
    private int _maxConversationsToKeep = 1000;

    // Build tools. The allowed folders are edited as one per line, which suits a
    // list that is usually one or two entries and occasionally hand-pasted.
    [ObservableProperty]
    private string _buildAllowedRoots = string.Empty;

    [ObservableProperty]
    private bool _buildAllowTests = true;

    [ObservableProperty]
    private int _buildTimeoutSeconds = 300;

    [ObservableProperty]
    private string _buildDotnetPath = "dotnet";

    [ObservableProperty]
    private string _buildCMakePath = string.Empty;

    [ObservableProperty]
    private string _buildMsBuildPath = string.Empty;

    [ObservableProperty]
    private bool _hasUnsavedChanges;

    public event EventHandler? SettingsSaved;

    /// <summary>Registered provider names, for the Default Provider list.</summary>
    public IReadOnlyList<string> ProviderOptions { get; }

    public SettingsViewModel(
        SettingsService settingsService,
        OpenRouterProvider openRouter,
        ILLMProviderFactory providerFactory,
        ILogger<SettingsViewModel> logger)
    {
        _settingsService = settingsService;
        _openRouter = openRouter;
        _logger = logger;
        ProviderOptions = providerFactory.GetAvailableProviders().ToList();

        _ = LoadSettingsAsync();
    }

    /// <summary>
    /// Fetches the full model list for the picker. On demand rather than when
    /// the window opens, so opening Settings for an unrelated reason does not
    /// make a network call.
    /// </summary>
    [RelayCommand]
    private async Task LoadOpenRouterModelsAsync()
    {
        IsLoadingOpenRouterModels = true;
        OpenRouterModelStatus = "Loading...";

        try
        {
            var models = await _openRouter.GetAllToolCapableModelsAsync();

            if (models.Count == 0)
            {
                OpenRouterModelStatus = "No models returned. Check the base URL and your connection.";
                return;
            }

            var selected = new HashSet<string>(
                _settingsService.GetCachedSettings().Providers.OpenRouterSelectedModels,
                StringComparer.OrdinalIgnoreCase);

            _allOpenRouterModels = models.Select(m => new OpenRouterModelChoice
            {
                Id = m.Id,
                IsFree = m.Capabilities.TryGetValue("free", out var free) && free is true,
                ContextWindow = m.ContextWindow,
                IsSelected = selected.Contains(m.Id)
            }).ToList();

            // A change to any checkbox is a change to the settings.
            foreach (var choice in _allOpenRouterModels)
                choice.PropertyChanged += (_, _) => HasUnsavedChanges = true;

            ApplyOpenRouterModelFilter();
            _logger.LogInformation("Loaded {Count} OpenRouter models for selection", models.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load OpenRouter models");
            OpenRouterModelStatus = $"Could not load models: {ex.Message}";
        }
        finally
        {
            IsLoadingOpenRouterModels = false;
        }
    }

    partial void OnOpenRouterModelFilterChanged(string value) => ApplyOpenRouterModelFilter();

    private void ApplyOpenRouterModelFilter()
    {
        if (_allOpenRouterModels.Count == 0)
            return;

        var term = OpenRouterModelFilter.Trim();

        var matches = string.IsNullOrEmpty(term)
            ? _allOpenRouterModels
            : _allOpenRouterModels
                .Where(m => m.Id.Contains(term, StringComparison.OrdinalIgnoreCase))
                .ToList();

        OpenRouterModels = new ObservableCollection<OpenRouterModelChoice>(matches);

        var chosen = _allOpenRouterModels.Count(m => m.IsSelected);
        OpenRouterModelStatus = chosen == 0
            ? $"Showing {matches.Count} of {_allOpenRouterModels.Count}. None selected, so all are offered."
            : $"Showing {matches.Count} of {_allOpenRouterModels.Count}. {chosen} selected.";
    }

    /// <summary>Clears the selection, which puts every model back in the dropdown.</summary>
    [RelayCommand]
    private void ClearOpenRouterModelSelection()
    {
        foreach (var choice in _allOpenRouterModels)
            choice.IsSelected = false;

        ApplyOpenRouterModelFilter();
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var settings = await _settingsService.LoadSettingsAsync();

            // Provider Settings
            OllamaBaseUrl = settings.Providers.OllamaBaseUrl;
            OllamaContextWindow = settings.Providers.OllamaContextWindow;
            FreeTokenApiKey = settings.Providers.FreeTokenApiKey;
            FreeTokenBaseUrl = settings.Providers.FreeTokenBaseUrl;
            OpenRouterApiKey = settings.Providers.OpenRouterApiKey;
            OpenRouterBaseUrl = settings.Providers.OpenRouterBaseUrl;
            AnthropicApiKey = settings.Providers.AnthropicApiKey;
            OpenAIApiKey = settings.Providers.OpenAIApiKey;
            GoogleApiKey = settings.Providers.GoogleApiKey;
            GoogleSearchEngineId = settings.Providers.GoogleSearchEngineId;

            ImageSource = settings.Tools.ImageSource;
            FallbackToCommons = settings.Tools.FallbackToCommons;
            WebSource = settings.Tools.WebSource;
            SafeSearch = settings.Tools.SafeSearch;

            // Mode prompts
            ChatModePrompt = settings.Modes.ChatPrompt;
            CodingModePrompt = settings.Modes.CodingPrompt;
            ScientificModePrompt = settings.Modes.ScientificPrompt;

            // Default Parameters
            DefaultTemperature = settings.Defaults.Temperature;
            DefaultMaxTokens = settings.Defaults.MaxTokens;
            DefaultTopP = settings.Defaults.TopP;
            DefaultProvider = settings.Defaults.DefaultProvider;
            DefaultModel = settings.Defaults.DefaultModel;

            BuildAllowedRoots = string.Join(Environment.NewLine, settings.BuildTools.AllowedRoots);
            BuildAllowTests = settings.BuildTools.AllowTests;
            BuildTimeoutSeconds = settings.BuildTools.TimeoutSeconds;
            BuildDotnetPath = settings.BuildTools.DotnetPath;
            BuildCMakePath = settings.BuildTools.CMakePath;
            BuildMsBuildPath = settings.BuildTools.MsBuildPath;

            AutoCompact = settings.Context.AutoCompact;
            CompactThresholdPercent = settings.Context.CompactThresholdPercent;
            KeepRecentMessages = settings.Context.KeepRecentMessages;
            FallbackWindowTokens = settings.Context.FallbackWindowTokens;

            // UI Preferences
            Theme = settings.UI.Theme;
            FontSize = settings.UI.FontSize;
            ShowTimestamps = settings.UI.ShowTimestamps;
            EnableMarkdown = settings.UI.EnableMarkdown;
            SidebarWidth = settings.UI.SidebarWidth;

            // Storage Settings
            ConversationsPath = settings.Storage.ConversationsPath;
            AutoSave = settings.Storage.AutoSave;
            MaxConversationsToKeep = settings.Storage.MaxConversationsToKeep;

            HasUnsavedChanges = false;

            _logger.LogInformation("Settings loaded successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load settings");
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            // Start from what is already on disk and overwrite only the fields
            // this window owns. Building a fresh AppSettings would silently drop
            // anything the UI does not surface - McpServers is hand-written and
            // has no editor, so constructing a new ToolSettings would reset it to
            // empty and destroy the user's MCP configuration on every save.
            var settings = _settingsService.GetCachedSettings();

            settings.Providers.OllamaBaseUrl = OllamaBaseUrl;

            // Negatives are meaningless to Ollama and would be sent verbatim;
            // 0 is the "you decide" case and stays as it is.
            settings.Providers.OllamaContextWindow = Math.Max(0, OllamaContextWindow);
            settings.Providers.FreeTokenApiKey = FreeTokenApiKey;
            settings.Providers.FreeTokenBaseUrl = FreeTokenBaseUrl;
            settings.Providers.OpenRouterApiKey = OpenRouterApiKey;
            settings.Providers.OpenRouterBaseUrl = OpenRouterBaseUrl;
            settings.Providers.AnthropicApiKey = AnthropicApiKey;
            settings.Providers.OpenAIApiKey = OpenAIApiKey;
            settings.Providers.GoogleApiKey = GoogleApiKey;
            settings.Providers.GoogleSearchEngineId = GoogleSearchEngineId;

            // Only overwrite the selection when the picker was actually loaded.
            // Saving an empty list from a window where the user never opened it
            // would silently discard their choices.
            if (_allOpenRouterModels.Count > 0)
            {
                settings.Providers.OpenRouterSelectedModels = _allOpenRouterModels
                    .Where(m => m.IsSelected)
                    .Select(m => m.Id)
                    .ToList();
            }

            settings.Tools.ImageSource = ImageSource;
            settings.Tools.FallbackToCommons = FallbackToCommons;
            settings.Tools.WebSource = WebSource;
            settings.Tools.SafeSearch = SafeSearch;

            settings.Modes.ChatPrompt = ChatModePrompt;
            settings.Modes.CodingPrompt = CodingModePrompt;
            settings.Modes.ScientificPrompt = ScientificModePrompt;

            settings.Defaults.Temperature = DefaultTemperature;
            settings.Defaults.MaxTokens = DefaultMaxTokens;
            settings.Defaults.TopP = DefaultTopP;
            settings.Defaults.DefaultProvider = DefaultProvider;
            settings.Defaults.DefaultModel = DefaultModel;

            // Clamped rather than trusted: these come from a text box, and a
            // threshold of 0 would compact after every single message while a
            // window of 0 would leave the meter with nothing to measure against.
            // Blank lines and stray whitespace dropped: a trailing newline in the
            // box would otherwise become an empty root, and an empty root would
            // be a root that matches nothing while still switching the tools on.
            settings.BuildTools.AllowedRoots = BuildAllowedRoots
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            settings.BuildTools.AllowTests = BuildAllowTests;
            settings.BuildTools.TimeoutSeconds = Math.Clamp(BuildTimeoutSeconds, 10, 3600);
            settings.BuildTools.DotnetPath = string.IsNullOrWhiteSpace(BuildDotnetPath)
                ? "dotnet"
                : BuildDotnetPath.Trim();
            settings.BuildTools.CMakePath = BuildCMakePath.Trim();
            settings.BuildTools.MsBuildPath = BuildMsBuildPath.Trim();

            settings.Context.AutoCompact = AutoCompact;
            settings.Context.CompactThresholdPercent = Math.Clamp(CompactThresholdPercent, 10, 100);
            settings.Context.KeepRecentMessages = Math.Max(0, KeepRecentMessages);
            settings.Context.FallbackWindowTokens = Math.Max(1024, FallbackWindowTokens);

            settings.UI.Theme = Theme;
            settings.UI.FontSize = FontSize;
            settings.UI.ShowTimestamps = ShowTimestamps;
            settings.UI.EnableMarkdown = EnableMarkdown;
            settings.UI.SidebarWidth = SidebarWidth;

            settings.Storage.ConversationsPath = ConversationsPath;
            settings.Storage.AutoSave = AutoSave;
            settings.Storage.MaxConversationsToKeep = MaxConversationsToKeep;

            await _settingsService.SaveSettingsAsync(settings);
            HasUnsavedChanges = false;

            _logger.LogInformation("Settings saved successfully");
            MessageBox.Show("Settings saved successfully!", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);

            SettingsSaved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save settings");
            MessageBox.Show($"Failed to save settings: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task ResetToDefaultsAsync()
    {
        var result = MessageBox.Show(
            "Are you sure you want to reset all settings to defaults?",
            "Confirm Reset",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            var defaultSettings = new AppSettings();
            await _settingsService.SaveSettingsAsync(defaultSettings);
            await LoadSettingsAsync();

            MessageBox.Show("Settings reset to defaults.", "Success",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    partial void OnOllamaBaseUrlChanged(string value) => HasUnsavedChanges = true;
    partial void OnOllamaContextWindowChanged(int value) => HasUnsavedChanges = true;
    partial void OnFreeTokenApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnFreeTokenBaseUrlChanged(string value) => HasUnsavedChanges = true;
    partial void OnOpenRouterApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnOpenRouterBaseUrlChanged(string value) => HasUnsavedChanges = true;
    partial void OnAnthropicApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnOpenAIApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnGoogleApiKeyChanged(string value) => HasUnsavedChanges = true;
    partial void OnGoogleSearchEngineIdChanged(string value) => HasUnsavedChanges = true;
    partial void OnImageSourceChanged(string value) => HasUnsavedChanges = true;
    partial void OnFallbackToCommonsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnWebSourceChanged(string value) => HasUnsavedChanges = true;
    partial void OnSafeSearchChanged(bool value) => HasUnsavedChanges = true;
    partial void OnChatModePromptChanged(string value) => HasUnsavedChanges = true;
    partial void OnCodingModePromptChanged(string value) => HasUnsavedChanges = true;
    partial void OnScientificModePromptChanged(string value) => HasUnsavedChanges = true;
    partial void OnDefaultTemperatureChanged(double value) => HasUnsavedChanges = true;
    partial void OnDefaultMaxTokensChanged(int value) => HasUnsavedChanges = true;
    partial void OnDefaultTopPChanged(double value) => HasUnsavedChanges = true;
    partial void OnDefaultProviderChanged(string value) => HasUnsavedChanges = true;
    partial void OnDefaultModelChanged(string value) => HasUnsavedChanges = true;
    partial void OnBuildAllowedRootsChanged(string value) => HasUnsavedChanges = true;
    partial void OnBuildAllowTestsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnBuildTimeoutSecondsChanged(int value) => HasUnsavedChanges = true;
    partial void OnBuildDotnetPathChanged(string value) => HasUnsavedChanges = true;
    partial void OnBuildCMakePathChanged(string value) => HasUnsavedChanges = true;
    partial void OnBuildMsBuildPathChanged(string value) => HasUnsavedChanges = true;
    partial void OnAutoCompactChanged(bool value) => HasUnsavedChanges = true;
    partial void OnCompactThresholdPercentChanged(int value) => HasUnsavedChanges = true;
    partial void OnKeepRecentMessagesChanged(int value) => HasUnsavedChanges = true;
    partial void OnFallbackWindowTokensChanged(int value) => HasUnsavedChanges = true;
    partial void OnThemeChanged(string value) => HasUnsavedChanges = true;
    partial void OnFontSizeChanged(int value) => HasUnsavedChanges = true;
    partial void OnShowTimestampsChanged(bool value) => HasUnsavedChanges = true;
    partial void OnEnableMarkdownChanged(bool value) => HasUnsavedChanges = true;
    partial void OnSidebarWidthChanged(int value) => HasUnsavedChanges = true;
    partial void OnConversationsPathChanged(string value) => HasUnsavedChanges = true;
    partial void OnAutoSaveChanged(bool value) => HasUnsavedChanges = true;
    partial void OnMaxConversationsToKeepChanged(int value) => HasUnsavedChanges = true;
}
