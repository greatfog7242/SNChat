using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;

namespace SNChat.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ILLMProviderFactory _providerFactory;
    private readonly IStorageService _storageService;
    private readonly ILogger<ChatViewModel> _logger;
    private CancellationTokenSource? _cancellationTokenSource;
    private ILLMProvider _currentProvider;

    [ObservableProperty]
    private ObservableCollection<Message> _messages = new();

    [ObservableProperty]
    private string _currentInput = string.Empty;

    [ObservableProperty]
    private Conversation? _currentConversation;

    [ObservableProperty]
    private bool _isStreaming;

    [ObservableProperty]
    private string _streamingContent = string.Empty;

    [ObservableProperty]
    private string _currentModel = "llama3.1:8b";

    [ObservableProperty]
    private ObservableCollection<string> _availableModels = new();

    [ObservableProperty]
    private string _currentProviderName = "Ollama";

    [ObservableProperty]
    private ObservableCollection<string> _availableProviders = new();

    public ChatViewModel(
        ILLMProviderFactory providerFactory,
        IStorageService storageService,
        ILogger<ChatViewModel> logger)
    {
        _providerFactory = providerFactory;
        _storageService = storageService;
        _logger = logger;

        // Load available providers
        foreach (var providerName in _providerFactory.GetAvailableProviders())
        {
            AvailableProviders.Add(providerName);
        }

        // Set default provider
        _currentProvider = _providerFactory.GetProvider(CurrentProviderName);

        StartNewConversation();
        _ = LoadAvailableModelsAsync();
    }

    partial void OnCurrentProviderNameChanged(string value)
    {
        try
        {
            _currentProvider = _providerFactory.GetProvider(value);
            _ = LoadAvailableModelsAsync();
            _logger.LogInformation("Switched to provider: {Provider}", value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch provider to {Provider}", value);
            MessageBox.Show($"Failed to switch provider: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadAvailableModelsAsync()
    {
        try
        {
            var models = await _currentProvider.GetAvailableModelsAsync();
            AvailableModels.Clear();

            foreach (var model in models)
            {
                AvailableModels.Add(model.Id);
            }

            if (AvailableModels.Count > 0 && !AvailableModels.Contains(CurrentModel))
            {
                CurrentModel = AvailableModels[0];
            }

            _logger.LogInformation("Loaded {Count} models from {Provider}", AvailableModels.Count, CurrentProviderName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load available models from {Provider}", CurrentProviderName);
            if (!AvailableModels.Contains(CurrentModel))
            {
                AvailableModels.Add(CurrentModel); // Fallback to default
            }
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentInput) || IsStreaming)
            return;

        var userMessage = new Message
        {
            Role = MessageRole.User,
            Content = CurrentInput.Trim(),
            Timestamp = DateTime.UtcNow
        };

        Messages.Add(userMessage);
        CurrentConversation?.Messages.Add(userMessage);

        var userInput = CurrentInput;
        CurrentInput = string.Empty;

        await GenerateResponseAsync(userInput);
    }

    [RelayCommand]
    private void NewConversation()
    {
        if (IsStreaming)
        {
            MessageBox.Show("Please wait for the current response to complete.", "Busy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        StartNewConversation();
    }

    [RelayCommand]
    private void CancelGeneration()
    {
        _cancellationTokenSource?.Cancel();
        _logger.LogInformation("User cancelled message generation");
    }

    private void StartNewConversation()
    {
        CurrentConversation = new Conversation
        {
            Title = "New Conversation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Metadata = new ConversationMetadata
            {
                ModelName = CurrentModel,
                Provider = CurrentProviderName
            }
        };

        Messages.Clear();
        StreamingContent = string.Empty;

        _logger.LogInformation("Started new conversation: {ConversationId} with provider {Provider}",
            CurrentConversation.Id, CurrentProviderName);
    }

    private async Task GenerateResponseAsync(string userInput)
    {
        IsStreaming = true;
        StreamingContent = string.Empty;
        _cancellationTokenSource = new CancellationTokenSource();

        var assistantMessage = new Message
        {
            Role = MessageRole.Assistant,
            Content = string.Empty,
            Timestamp = DateTime.UtcNow
        };

        Messages.Add(assistantMessage);

        try
        {
            var request = new GenerateRequest
            {
                Model = CurrentModel,
                Messages = CurrentConversation!.Messages.ToList(),
                Parameters = new ModelParameters
                {
                    Temperature = 0.7,
                    MaxTokens = 2000
                }
            };

            _logger.LogDebug("Sending request to {Provider}: {MessageCount} messages", CurrentProviderName, request.Messages.Count);

            await foreach (var chunk in _currentProvider.GenerateStreamAsync(request))
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    StreamingContent += chunk.Content;
                    assistantMessage.Content = StreamingContent;
                }

                if (chunk.IsFinal && chunk.Metadata != null)
                {
                    _logger.LogInformation(
                        "Response complete. Tokens: {PromptTokens} in, {CompletionTokens} out, Duration: {Duration}ns",
                        chunk.Metadata.PromptEvalCount,
                        chunk.Metadata.EvalCount,
                        chunk.Metadata.TotalDuration
                    );
                }
            }

            CurrentConversation!.Messages.Add(assistantMessage);
            await SaveConversationAsync();

            _logger.LogDebug("Response generation completed successfully");
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Content = StreamingContent + "\n\n[Cancelled by user]";
            _logger.LogInformation("Response generation was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating response");
            assistantMessage.Content = $"Error: {ex.Message}";
            MessageBox.Show($"Failed to generate response: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsStreaming = false;
            StreamingContent = string.Empty;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    private async Task SaveConversationAsync()
    {
        try
        {
            if (CurrentConversation != null)
            {
                CurrentConversation.UpdatedAt = DateTime.UtcNow;

                // Auto-generate title from first user message if still default
                if (CurrentConversation.Title == "New Conversation" && CurrentConversation.Messages.Count > 0)
                {
                    var firstUserMessage = CurrentConversation.Messages.FirstOrDefault(m => m.Role == MessageRole.User);
                    if (firstUserMessage != null)
                    {
                        var title = firstUserMessage.Content.Length > 50
                            ? firstUserMessage.Content.Substring(0, 47) + "..."
                            : firstUserMessage.Content;
                        CurrentConversation.Title = title;
                    }
                }

                await _storageService.SaveConversationAsync(CurrentConversation);
                _logger.LogDebug("Conversation saved: {ConversationId}", CurrentConversation.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save conversation");
        }
    }
}
