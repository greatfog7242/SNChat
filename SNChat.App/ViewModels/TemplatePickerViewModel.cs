using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.App.ViewModels;

public partial class TemplatePickerViewModel : ObservableObject
{
    private readonly TemplateService _templateService;
    private readonly ILogger<TemplatePickerViewModel> _logger;

    [ObservableProperty]
    private ObservableCollection<PromptTemplate> _templates = new();

    [ObservableProperty]
    private PromptTemplate? _selectedTemplate;

    /// <summary>One entry per {{placeholder}} in the selected template.</summary>
    [ObservableProperty]
    private ObservableCollection<TemplateVariable> _variables = new();

    [ObservableProperty]
    private string _preview = string.Empty;

    [ObservableProperty]
    private string _searchText = string.Empty;

    private List<PromptTemplate> _allTemplates = new();

    /// <summary>Raised when the user confirms; carries the filled-in prompt.</summary>
    public event EventHandler<TemplateResult>? TemplateAccepted;

    public TemplatePickerViewModel(
        TemplateService templateService,
        ILogger<TemplatePickerViewModel> logger)
    {
        _templateService = templateService;
        _logger = logger;

        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await _templateService.SeedDefaultsIfEmptyAsync();
            _allTemplates = await _templateService.LoadAllAsync();
            ApplyFilter();

            _logger.LogInformation("Loaded {Count} prompt templates", _allTemplates.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load prompt templates");
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var matches = string.IsNullOrWhiteSpace(SearchText)
            ? _allTemplates
            : _allTemplates.Where(t =>
                  t.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                  t.Description.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ||
                  t.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
              .ToList();

        Templates.Clear();
        foreach (var template in matches)
            Templates.Add(template);
    }

    partial void OnSelectedTemplateChanged(PromptTemplate? value)
    {
        // Detach the old handlers so stale entries stop driving the preview.
        foreach (var variable in Variables)
            variable.PropertyChanged -= OnVariableChanged;

        Variables.Clear();

        if (value == null)
        {
            Preview = string.Empty;
            return;
        }

        foreach (var name in value.GetVariables())
        {
            var variable = new TemplateVariable { Name = name };
            variable.PropertyChanged += OnVariableChanged;
            Variables.Add(variable);
        }

        UpdatePreview();
    }

    private void OnVariableChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) =>
        UpdatePreview();

    private void UpdatePreview()
    {
        Preview = SelectedTemplate?.Render(CurrentValues()) ?? string.Empty;
    }

    private Dictionary<string, string> CurrentValues() =>
        Variables.ToDictionary(v => v.Name, v => v.Value ?? string.Empty);

    [RelayCommand]
    private void Accept()
    {
        if (SelectedTemplate == null)
            return;

        var values = CurrentValues();

        TemplateAccepted?.Invoke(this, new TemplateResult
        {
            Prompt = SelectedTemplate.Render(values),
            SystemPrompt = SelectedTemplate.RenderSystemPrompt(values),
            TemplateName = SelectedTemplate.Name
        });
    }
}

public partial class TemplateVariable : ObservableObject
{
    public string Name { get; init; } = string.Empty;

    /// <summary>Shows "target language" rather than "target_language".</summary>
    public string DisplayName =>
        Name.Replace('_', ' ').Replace('-', ' ');

    [ObservableProperty]
    private string? _value = string.Empty;
}

public class TemplateResult
{
    public string Prompt { get; init; } = string.Empty;
    public string SystemPrompt { get; init; } = string.Empty;
    public string TemplateName { get; init; } = string.Empty;
}
