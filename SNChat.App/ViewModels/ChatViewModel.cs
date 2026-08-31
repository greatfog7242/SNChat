using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;

namespace SNChat.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ILLMProviderFactory _providerFactory;
    private readonly IStorageService _storageService;
    private readonly IToolRegistry _toolRegistry;
    private readonly AttachmentService _attachmentService;
    private readonly SettingsService _settingsService;
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

    /// <summary>Transient progress text such as "Using web_search...".</summary>
    [ObservableProperty]
    private string _statusMessage = string.Empty;

    /// <summary>
    /// Sent ahead of the conversation on every request. Set when a template
    /// carries one; empty otherwise.
    /// </summary>
    [ObservableProperty]
    private string _systemPrompt = string.Empty;

    /// <summary>Name of the template in use, shown in the toolbar.</summary>
    [ObservableProperty]
    private string _activeTemplateName = string.Empty;

    public bool HasSystemPrompt => !string.IsNullOrWhiteSpace(SystemPrompt);

    /// <summary>Files dropped but not yet sent with a message.</summary>
    [ObservableProperty]
    private ObservableCollection<Attachment> _pendingAttachments = new();

    public bool HasPendingAttachments => PendingAttachments.Count > 0;

    // Left empty on purpose: the real value comes from whatever the provider
    // reports as installed. A hardcoded guess produces 404 "model not found".
    [ObservableProperty]
    private string _currentModel = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _availableModels = new();

    [ObservableProperty]
    private string _currentProviderName = "Ollama";

    [ObservableProperty]
    private ObservableCollection<string> _availableProviders = new();

    /// <summary>
    /// When off, no tool definitions are sent, keeping ordinary chats fast.
    /// </summary>
    [ObservableProperty]
    private bool _webSearchEnabled;

    public event EventHandler? ConversationSaved;

    public ChatViewModel(
        ILLMProviderFactory providerFactory,
        IStorageService storageService,
        IToolRegistry toolRegistry,
        AttachmentService attachmentService,
        SettingsService settingsService,
        ILogger<ChatViewModel> logger)
    {
        _providerFactory = providerFactory;
        _storageService = storageService;
        _toolRegistry = toolRegistry;
        _attachmentService = attachmentService;
        _settingsService = settingsService;
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
            if (!string.IsNullOrEmpty(CurrentModel) && !AvailableModels.Contains(CurrentModel))
            {
                AvailableModels.Add(CurrentModel); // Keep whatever we already had
            }
        }
    }

    [RelayCommand]
    private async Task SendMessageAsync()
    {
        // An attachment on its own is a valid message; the file is the content.
        if ((string.IsNullOrWhiteSpace(CurrentInput) && !HasPendingAttachments) || IsStreaming)
            return;

        var attachments = PendingAttachments.ToList();
        var typed = CurrentInput.Trim();

        // File contents go ahead of the typed text so the question reads last,
        // which keeps the model's attention on what is being asked.
        var content = attachments.Count > 0
            ? AttachmentService.BuildContextBlock(attachments) + typed
            : typed;

        var userMessage = new Message
        {
            Role = MessageRole.User,
            Content = content,
            Timestamp = DateTime.UtcNow,
            Attachments = attachments
        };

        Messages.Add(userMessage);
        CurrentConversation?.Messages.Add(userMessage);

        CurrentInput = string.Empty;
        PendingAttachments.Clear();
        OnPropertyChanged(nameof(HasPendingAttachments));

        // Save conversation immediately after user message so it appears in the list
        // even if generation is cancelled or fails
        await SaveConversationAsync();

        await GenerateResponseAsync(typed);
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

    /// <summary>
    /// Copies dropped files into the current conversation's folder and queues
    /// them for the next message. Reports which ones were rejected rather than
    /// dropping them silently.
    /// </summary>
    public async Task AddAttachmentsAsync(IEnumerable<string> paths)
    {
        if (CurrentConversation == null)
            return;

        var rejected = new List<string>();

        foreach (var path in paths)
        {
            var attachment = await _attachmentService.AttachAsync(path, CurrentConversation.Id);

            if (attachment == null)
                rejected.Add(Path.GetFileName(path));
            else
                PendingAttachments.Add(attachment);
        }

        OnPropertyChanged(nameof(HasPendingAttachments));

        if (rejected.Count > 0)
        {
            MessageBox.Show(
                $"These files could not be attached:\n\n{string.Join("\n", rejected)}\n\n" +
                $"Files must exist and be under {AttachmentService.MaxFileSizeBytes / (1024 * 1024)} MB.",
                "Some files were skipped",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void RemoveAttachment(Attachment? attachment)
    {
        if (attachment == null)
            return;

        PendingAttachments.Remove(attachment);
        OnPropertyChanged(nameof(HasPendingAttachments));
    }

    /// <summary>
    /// Puts a template's filled-in prompt into the input box rather than sending
    /// it, so it can still be edited before going out.
    /// </summary>
    public void ApplyTemplate(string prompt, string systemPrompt, string templateName)
    {
        CurrentInput = prompt;
        ActiveTemplateName = templateName;

        if (!string.IsNullOrWhiteSpace(systemPrompt))
        {
            SystemPrompt = systemPrompt;
            OnPropertyChanged(nameof(HasSystemPrompt));
        }

        _logger.LogInformation("Applied template {Name} (system prompt: {HasSystem})",
            templateName, !string.IsNullOrWhiteSpace(systemPrompt));
    }

    [RelayCommand]
    private void ClearTemplate()
    {
        SystemPrompt = string.Empty;
        ActiveTemplateName = string.Empty;
        OnPropertyChanged(nameof(HasSystemPrompt));
    }

    [RelayCommand]
    private void CopyMessage(Message? message)
    {
        if (message == null)
            return;

        TrySetClipboard(message.Content, "Message copied");
    }

    [RelayCommand]
    private void CopyCode(Message? message)
    {
        if (message == null)
            return;

        var code = MarkdownCode.ExtractBlocks(message.Content);
        if (string.IsNullOrEmpty(code))
            return;

        TrySetClipboard(code, "Code copied");
    }

    /// <summary>
    /// The clipboard is held by other processes often enough that Copy throws;
    /// a failed copy should not surface as an unhandled exception.
    /// </summary>
    private void TrySetClipboard(string text, string what)
    {
        try
        {
            Clipboard.SetText(text);
            _logger.LogDebug("{What} ({Length} chars)", what, text.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write to the clipboard");
            MessageBox.Show("Could not access the clipboard. Another application may be using it.",
                "Copy failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
                },
                SystemPrompt = string.IsNullOrWhiteSpace(SystemPrompt) ? null : SystemPrompt,
                CancellationToken = _cancellationTokenSource.Token,
                Tools = WebSearchEnabled
                    ? _toolRegistry.GetTools()
                    : Array.Empty<ITool>(),
                MaxToolIterations = _settingsService.GetCachedSettings().Tools.MaxToolIterations
            };

            _logger.LogDebug("Sending request to {Provider}: {MessageCount} messages, {ToolCount} tool(s)",
                CurrentProviderName, request.Messages.Count, request.Tools.Count);

            await foreach (var chunk in _currentProvider.GenerateStreamAsync(request))
            {
                if (_cancellationTokenSource.Token.IsCancellationRequested)
                    break;

                // Progress notices are shown live but never persisted into the answer.
                if (chunk.IsStatus)
                {
                    StatusMessage = chunk.Content;
                    continue;
                }

                if (!string.IsNullOrEmpty(chunk.Content))
                {
                    StatusMessage = string.Empty;
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
            StatusMessage = string.Empty;
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }
    }

    public void LoadConversation(Conversation conversation)
    {
        CurrentConversation = conversation;

        if (!string.IsNullOrEmpty(conversation.Metadata.Provider))
            CurrentProviderName = conversation.Metadata.Provider;

        // A conversation can reference a model that has since been removed from the
        // provider. Sending it anyway makes Ollama reply 404 "model not found", so
        // fall back to something actually installed.
        var savedModel = conversation.Metadata.ModelName;
        if (!string.IsNullOrEmpty(savedModel) && AvailableModels.Contains(savedModel))
        {
            CurrentModel = savedModel;
        }
        else if (AvailableModels.Count > 0)
        {
            _logger.LogWarning(
                "Conversation model {SavedModel} is not available; falling back to {Fallback}",
                savedModel, AvailableModels[0]);
            CurrentModel = AvailableModels[0];
        }

        Messages.Clear();
        foreach (var message in conversation.Messages)
        {
            Messages.Add(message);
        }

        _logger.LogInformation("Loaded conversation: {Title} with {Count} messages",
            conversation.Title, conversation.Messages.Count);
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
                        // For messages with attachments, generate a better title
                        string titleContent;
                        if (firstUserMessage.Attachments.Count > 0)
                        {
                            // Remove attachment context block to get just the user's text
                            var content = firstUserMessage.Content;
                            var lines = content.Split('\n');
                            var userText = string.Join(" ", lines.Where(l =>
                                !l.StartsWith("---") &&
                                !l.StartsWith("```") &&
                                !string.IsNullOrWhiteSpace(l))).Trim();

                            // If no user text, use attachment filename
                            if (string.IsNullOrWhiteSpace(userText))
                            {
                                var firstAttachment = firstUserMessage.Attachments[0];
                                titleContent = firstAttachment.Type == AttachmentType.Image
                                    ? $"Image: {firstAttachment.FileName}"
                                    : $"File: {firstAttachment.FileName}";
                            }
                            else
                            {
                                titleContent = userText;
                            }
                        }
                        else
                        {
                            titleContent = firstUserMessage.Content;
                        }

                        var title = titleContent.Length > 50
                            ? titleContent.Substring(0, 47) + "..."
                            : titleContent;
                        CurrentConversation.Title = title;
                    }
                }

                await _storageService.SaveConversationAsync(CurrentConversation);
                _logger.LogDebug("Conversation saved: {ConversationId}", CurrentConversation.Id);

                // Notify that conversation was saved
                ConversationSaved?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save conversation");
        }
    }
}
