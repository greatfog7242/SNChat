using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
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

    /// <summary>
    /// False while the constructor is restoring the previous session, so that
    /// restoring a selection is not itself recorded as a new one.
    /// </summary>
    private bool _selectionRestored;

    /// <summary>
    /// Drives the elapsed-time readout. A DispatcherTimer rather than a task
    /// loop because it ticks on the UI thread, so the bound property can be
    /// updated straight from the handler.
    /// </summary>
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTime _generationStartedAt;

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
    /// How long the current reply has been running, as "124s". A local model can
    /// spend minutes on one turn, and without a moving number there is nothing
    /// to tell a working model from a hung one. Holds the final duration once
    /// the reply finishes, so the cost of the turn stays visible.
    /// </summary>
    [ObservableProperty]
    private string _elapsedTime = string.Empty;

    /// <summary>True once a reply has finished, to label the held duration.</summary>
    [ObservableProperty]
    private bool _hasElapsedTime;

    /// <summary>
    /// Running cost of the open conversation, as "24,180 in · 9,412 out".
    /// Empty when nothing has been spent yet, so a fresh conversation does not
    /// show a row of zeroes.
    /// </summary>
    [ObservableProperty]
    private string _conversationTokenSummary = string.Empty;

    public bool HasConversationTokens => !string.IsNullOrEmpty(ConversationTokenSummary);

    private void UpdateConversationTokenSummary()
    {
        var metadata = CurrentConversation?.Metadata;
        var input = metadata?.TotalPromptTokens ?? 0;
        var output = metadata?.TotalCompletionTokens ?? 0;

        ConversationTokenSummary = input == 0 && output == 0
            ? string.Empty
            : $"{input:N0} in · {output:N0} out";

        OnPropertyChanged(nameof(HasConversationTokens));
    }

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
    /// Initialised from Tools.EnabledByDefault, which starts on.
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

        RestoreLastSelection();

        WebSearchEnabled = _settingsService.GetCachedSettings().Tools.EnabledByDefault;

        _elapsedTimer.Tick += (_, _) => ElapsedTime = FormatElapsed(DateTime.UtcNow - _generationStartedAt);

        _currentProvider = _providerFactory.GetProvider(CurrentProviderName);

        StartNewConversation();
        _ = LoadAvailableModelsAsync();

        // Only from here on does a change represent a choice worth saving;
        // everything above is the restore itself.
        _selectionRestored = true;
    }

    /// <summary>
    /// Puts back the provider and model from the previous session, falling back
    /// to the configured defaults.
    ///
    /// The backing fields are assigned directly: setting the properties would
    /// fire OnCurrentProviderNameChanged, which resolves the provider and
    /// starts a model load, before this constructor has finished wiring itself
    /// up.
    /// </summary>
    // Assigning the [ObservableProperty] backing fields is the point here, not
    // an oversight: the generated setters raise change notifications, and this
    // runs before the view model is ready to react to them.
#pragma warning disable MVVMTK0034
    private void RestoreLastSelection()
    {
        var defaults = _settingsService.GetCachedSettings().Defaults;

        var provider = string.IsNullOrWhiteSpace(defaults.LastProvider)
            ? defaults.DefaultProvider
            : defaults.LastProvider;

        // A provider can disappear between runs, by rename or by being
        // unregistered. Falling back keeps the app usable instead of throwing
        // out of the constructor and taking the window with it.
        if (!AvailableProviders.Contains(provider))
        {
            if (!string.IsNullOrEmpty(provider))
                _logger.LogWarning("Provider {Provider} is no longer available; using {Fallback}",
                    provider, AvailableProviders.FirstOrDefault());

            provider = AvailableProviders.FirstOrDefault() ?? "Ollama";
        }

        _currentProviderName = provider;

        // Left for LoadAvailableModelsAsync to validate: the model list is not
        // known yet, and it already replaces a model the provider does not offer.
        _currentModel = string.IsNullOrWhiteSpace(defaults.LastModel)
            ? defaults.DefaultModel
            : defaults.LastModel;

        _logger.LogInformation("Restored selection: {Provider} / {Model}",
            _currentProviderName,
            string.IsNullOrEmpty(_currentModel) ? "(first available)" : _currentModel);
    }
#pragma warning restore MVVMTK0034

    /// <summary>
    /// Records the current pick so the next launch starts here. Fire-and-forget:
    /// a settings write must not block the UI thread on a dropdown change, and a
    /// failure to remember a preference is not worth interrupting the user over.
    /// </summary>
    private void PersistSelection()
    {
        if (!_selectionRestored)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                var settings = _settingsService.GetCachedSettings();
                settings.Defaults.LastProvider = CurrentProviderName;
                settings.Defaults.LastModel = CurrentModel;
                await _settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save the provider/model selection");
            }
        });
    }

    /// <summary>
    /// Runs the elapsed-time readout for exactly as long as a reply is being
    /// produced. Driven off IsStreaming rather than started and stopped at the
    /// call sites, so a reply that ends by cancellation or by error stops the
    /// clock the same way a completed one does.
    /// </summary>
    partial void OnIsStreamingChanged(bool value)
    {
        if (value)
        {
            _generationStartedAt = DateTime.UtcNow;
            ElapsedTime = FormatElapsed(TimeSpan.Zero);
            HasElapsedTime = false;
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();

            // Tick only fires on whole seconds, so a reply finishing between
            // ticks would otherwise leave a stale number on screen.
            ElapsedTime = FormatElapsed(DateTime.UtcNow - _generationStartedAt);
            HasElapsedTime = true;
        }
    }

    /// <summary>
    /// A running total of seconds for the whole task, never rolling over into
    /// minutes. Rolling over resets the visible number to 00 at the one minute
    /// mark, which reads as the counter having stopped rather than passed a
    /// minute - and these tasks routinely run for several.
    /// </summary>
    private static string FormatElapsed(TimeSpan elapsed) =>
        $"{(int)elapsed.TotalSeconds}s";

    partial void OnCurrentModelChanged(string value) => PersistSelection();

    partial void OnCurrentProviderNameChanged(string value)
    {
        try
        {
            _currentProvider = _providerFactory.GetProvider(value);
            PersistSelection();
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
        UpdateConversationTokenSummary();

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
            Timestamp = DateTime.UtcNow,
            // Captured now rather than read at render time, so a reply keeps
            // showing what produced it after the selection is changed.
            Provider = CurrentProviderName,
            ModelName = CurrentModel
        };

        Messages.Add(assistantMessage);

        // Reported by the provider when the stream ends; used to tell a model
        // that ran out of budget from one that simply said nothing.
        int? completionTokens = null;

        try
        {
            var defaults = _settingsService.GetCachedSettings().Defaults;

            var request = new GenerateRequest
            {
                Model = CurrentModel,
                Messages = CurrentConversation!.Messages.ToList(),
                // These were hardcoded, so the temperature, token limit and top-p
                // in Settings were written but never sent.
                Parameters = new ModelParameters
                {
                    Temperature = defaults.Temperature,
                    MaxTokens = defaults.MaxTokens,
                    TopP = defaults.TopP
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
                    completionTokens = chunk.Metadata.EvalCount;

                    assistantMessage.CompletionTokens = chunk.Metadata.EvalCount;

                    // The prompt count arrives with the reply but describes the
                    // input, so it belongs on the message that prompted it.
                    var prompting = Messages.LastOrDefault(m => m.Role == MessageRole.User);
                    if (prompting != null)
                        prompting.PromptTokens = chunk.Metadata.PromptEvalCount;

                    if (CurrentConversation != null)
                    {
                        CurrentConversation.Metadata.TotalPromptTokens += chunk.Metadata.PromptEvalCount ?? 0;
                        CurrentConversation.Metadata.TotalCompletionTokens += chunk.Metadata.EvalCount ?? 0;
                        UpdateConversationTokenSummary();
                    }

                    _logger.LogInformation(
                        "Response complete. Tokens: {PromptTokens} in, {CompletionTokens} out, Duration: {Duration}ns",
                        chunk.Metadata.PromptEvalCount,
                        chunk.Metadata.EvalCount,
                        chunk.Metadata.TotalDuration
                    );
                }
            }

            // A reasoning model streams its thinking separately from its answer,
            // and thinking is shown as status rather than kept. So a model that
            // spends its whole token budget reasoning finishes with nothing to
            // show, and the turn just looks silently blank. Say what happened
            // instead, since the fix is a setting the user can change.
            if (string.IsNullOrWhiteSpace(assistantMessage.Content))
            {
                var limit = defaults.MaxTokens;

                assistantMessage.Content = completionTokens >= limit
                    ? $"⚠️ The model used its entire {limit}-token budget before writing an answer. " +
                      "Reasoning models can spend the whole allowance thinking. " +
                      "Raise Max Tokens in Settings → Defaults, or use a model that reasons less."
                    : "⚠️ The model returned an empty response.";

                _logger.LogWarning(
                    "Empty response: {Tokens} completion tokens against a {Limit}-token limit",
                    completionTokens, limit);
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

        // Deliberately leaves the provider and model alone. The metadata records
        // what produced these messages, which is history rather than a
        // requirement: messages carry no provider-specific content, so an old
        // conversation continues perfectly well under whichever model is
        // selected now.
        //
        // Adopting it instead meant opening any conversation from before a
        // provider was added switched away from the current one - and since the
        // selection is saved as it changes, that overwrote the remembered choice
        // for good. It also raced: assigning the provider starts an async model
        // load, so the checks that followed read the previous provider's list.
        if (!string.IsNullOrEmpty(conversation.Metadata.Provider) &&
            conversation.Metadata.Provider != CurrentProviderName)
        {
            _logger.LogInformation(
                "Conversation was created with {Provider}/{Model}; continuing with {CurrentProvider}/{CurrentModel}",
                conversation.Metadata.Provider, conversation.Metadata.ModelName,
                CurrentProviderName, CurrentModel);
        }

        Messages.Clear();
        foreach (var message in conversation.Messages)
        {
            Messages.Add(message);
        }

        UpdateConversationTokenSummary();

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

                // Record what is actually producing the messages. The metadata
                // was only ever set when the conversation was created, so one
                // continued under a different model kept reporting the original
                // in the sidebar.
                CurrentConversation.Metadata.Provider = CurrentProviderName;
                CurrentConversation.Metadata.ModelName = CurrentModel;

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
