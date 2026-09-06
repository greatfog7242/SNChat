using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SNChat.BuildTools;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;
using SNChat.LLM.Services;

namespace SNChat.App.ViewModels;

public partial class ChatViewModel : ObservableObject
{
    private readonly ILLMProviderFactory _providerFactory;
    private readonly IStorageService _storageService;
    private readonly IToolRegistry _toolRegistry;
    private readonly AttachmentService _attachmentService;
    private readonly SettingsService _settingsService;
    private readonly WebImageCacheService _webImageCache;
    private readonly ConversationCompactor _compactor;
    private readonly ProjectService _projectService;
    private readonly ProjectContext _projectContext;
    private readonly RulesService _rules;
    private readonly AgentSignals _signals;
    private readonly GitCheckpointService _checkpoints;

    /// <summary>
    /// Why the last turn ended. The cancellation token source is disposed and
    /// nulled in GenerateResponseAsync's finally, so by the time a loop asks
    /// there is nothing left to inspect - these survive on purpose.
    /// </summary>
    private bool _lastTurnCancelled;
    private bool _lastTurnFailed;
    private readonly ILogger<ChatViewModel> _logger;

    /// <summary>
    /// True while the picker is being moved to match a conversation that was
    /// just opened, so that restoring a choice is not recorded as making one.
    /// </summary>
    private bool _applyingProjectFromConversation;
    private CancellationTokenSource? _cancellationTokenSource;
    private ILLMProvider _currentProvider;

    /// <summary>
    /// Context window per model id, as the provider reported it. Kept because
    /// AvailableModels holds only ids, and the meter needs the capacity of
    /// whichever one is selected.
    /// </summary>
    private readonly Dictionary<string, long> _modelContextWindows = new();

    // The provider counts the prompt for real on every turn, which beats any
    // estimate this side of the wire. These hold the most recent such count and
    // how many messages it covered, so only what came after has to be guessed.
    private int? _measuredPromptTokens;
    private int _measuredThrough;

    /// <summary>
    /// How many messages the in-flight request carried, held until the count for
    /// it comes back with the reply.
    /// </summary>
    private int _requestedMessageCount;

    /// <summary>
    /// What the tool definitions cost, worked out once. Serializing nineteen
    /// MCP schemas is far too much to redo on every keystroke, and the registry
    /// is fixed once the servers have connected at startup.
    /// </summary>
    private int? _toolTokens;

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

        var cost = metadata?.TotalCost.HasValue == true
            ? $"${metadata.TotalCost!.Value:0.######}"
            : "cost n/a";

        ConversationTokenSummary = input == 0 && output == 0
            ? string.Empty
            : $"{input:N0} in · {output:N0} out · {cost}";

        OnPropertyChanged(nameof(HasConversationTokens));
    }

    /// <summary>
    /// How full the context window is, 0-100, for the meter in the input bar.
    /// Unlike the token summary beside it this is not a running total: the
    /// history is resent whole on every turn, so this is what the *next* request
    /// will occupy, and compacting sends it back down.
    /// </summary>
    [ObservableProperty]
    private int _contextPercent;

    /// <summary>Reads as "62% · 5.1k/8k".</summary>
    [ObservableProperty]
    private string _contextSummary = string.Empty;

    /// <summary>
    /// "Normal", "Warning" or "Critical", which the view colours the bar by.
    /// A string rather than a brush so the palette stays in the XAML with the
    /// rest of the styling.
    /// </summary>
    [ObservableProperty]
    private string _contextLevel = "Normal";

    /// <summary>False when the model's capacity is unknown, leaving nothing to measure against.</summary>
    [ObservableProperty]
    private bool _hasContextUsage;

    [ObservableProperty]
    private bool _isCompacting;

    /// <summary>
    /// True for the whole of an automatic run, across all of its turns. Distinct
    /// from IsStreaming, which goes false between them.
    /// </summary>
    [ObservableProperty]
    private bool _isAgentRunning;

    /// <summary>Where an automatic run has got to, or why it stopped.</summary>
    [ObservableProperty]
    private string _agentStatus = string.Empty;

    public bool HasAgentStatus => !string.IsNullOrEmpty(AgentStatus);

    partial void OnAgentStatusChanged(string value) => OnPropertyChanged(nameof(HasAgentStatus));

    public bool CanCompact => !IsStreaming && !IsCompacting && CurrentConversation != null;

    partial void OnIsCompactingChanged(bool value) => OnPropertyChanged(nameof(CanCompact));

    partial void OnCurrentConversationChanged(Conversation? value) =>
        OnPropertyChanged(nameof(CanCompact));

    /// <summary>
    /// The capacity of the selected model, or the configured fallback where the
    /// provider does not report one - Ollama's model list carries no context
    /// length, so most local models land here.
    /// </summary>
    private int ContextWindowTokens
    {
        get
        {
            if (_modelContextWindows.TryGetValue(CurrentModel, out var reported) && reported > 0)
                return (int)Math.Min(reported, int.MaxValue);

            return _settingsService.GetCachedSettings().Context.FallbackWindowTokens;
        }
    }

    /// <summary>
    /// Recomputes the meter. Called whenever anything that goes into the next
    /// prompt changes - a message, the model, or the text being typed.
    /// </summary>
    private void UpdateContextUsage()
    {
        var window = ContextWindowTokens;
        var live = ContextMeter.LiveMessages(CurrentConversation?.Messages ?? new List<Message>());

        var usage = ContextMeter.Measure(
            live, BuildSystemPrompt(), window, _measuredPromptTokens, _measuredThrough,
            toolTokens: WebSearchEnabled ? ToolTokens : 0);

        // What is in the input box has not been sent, but it is about to be, so
        // pasting a large file shows up on the meter before it costs anything.
        usage = usage with { UsedTokens = usage.UsedTokens + TokenEstimator.Estimate(CurrentInput) };

        HasContextUsage = usage.IsKnown;
        ContextPercent = usage.Percent;

        var threshold = _settingsService.GetCachedSettings().Context.CompactThresholdPercent;

        ContextLevel = usage.HasReached(threshold) ? "Critical"
            : usage.HasReached(threshold - 10) ? "Warning"
            : "Normal";

        ContextSummary = usage.IsKnown
            ? $"{usage.Percent}% · {Abbreviate(usage.UsedTokens)}/{Abbreviate(window)}"
            : string.Empty;
    }

    private int ToolTokens => _toolTokens ??= TokenEstimator.EstimateTools(_toolRegistry.GetTools());

    /// <summary>
    /// Turning tools off takes their definitions out of the prompt, which on a
    /// large MCP setup is most of it.
    /// </summary>
    partial void OnWebSearchEnabledChanged(bool value) => UpdateContextUsage();

    /// <summary>"512", "5.1k", "128k" - short enough to sit in the input bar.</summary>
    private static string Abbreviate(int tokens) => tokens switch
    {
        < 1000 => tokens.ToString(),
        < 10000 => $"{tokens / 1000.0:0.#}k",
        _ => $"{tokens / 1000}k"
    };

    partial void OnCurrentInputChanged(string value) => UpdateContextUsage();

    /// <summary>
    /// Throws away the provider's last prompt count and re-reads the meter from
    /// an estimate. Needed whenever the history stops being what that count
    /// described - a different conversation, or one just compacted.
    /// </summary>
    private void ResetContextMeasurement()
    {
        _measuredPromptTokens = null;
        _measuredThrough = 0;
        UpdateContextUsage();
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

    /// <summary>
    /// The answering style in use. Its standing instruction is combined with
    /// any template's system prompt when a request goes out.
    /// </summary>
    [ObservableProperty]
    private string _currentMode = ChatMode.Chat;

    public IReadOnlyList<string> AvailableModes { get; } = ChatMode.All;

    partial void OnCurrentModeChanged(string value) => PersistSelection();

    /// <summary>
    /// The instructions sent ahead of the conversation, from most general to
    /// most specific: rules that always apply, then this project's rules, then
    /// the answering mode, then whatever a template asked for.
    ///
    /// Called whenever the context meter refreshes, so the rules files behind it
    /// are cached rather than read each time.
    /// </summary>
    private string BuildSystemPrompt() =>
        SystemPromptComposer.Compose(
            _rules.ReadGlobal(),
            _rules.ReadForProject(IsRealProject(CurrentProject) ? CurrentProject!.RootPath : null),
            _settingsService.GetCachedSettings().Modes.PromptFor(CurrentMode),
            SystemPrompt);

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

    /// <summary>Projects to choose from, with "(no project)" first.</summary>
    [ObservableProperty]
    private ObservableCollection<Project> _availableProjects = new();

    /// <summary>
    /// The project this conversation is working in. Decides which folder the
    /// build and run tools may touch, and how much the assistant may do on its own.
    /// </summary>
    [ObservableProperty]
    private Project? _currentProject;

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
        WebImageCacheService webImageCache,
        ConversationCompactor compactor,
        ProjectService projectService,
        ProjectContext projectContext,
        RulesService rules,
        AgentSignals signals,
        GitCheckpointService checkpoints,
        ILogger<ChatViewModel> logger)
    {
        _providerFactory = providerFactory;
        _storageService = storageService;
        _toolRegistry = toolRegistry;
        _attachmentService = attachmentService;
        _settingsService = settingsService;
        _webImageCache = webImageCache;
        _compactor = compactor;
        _projectService = projectService;
        _projectContext = projectContext;
        _rules = rules;
        _signals = signals;
        _checkpoints = checkpoints;
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
        _ = LoadProjectsAsync();

        // Only from here on does a change represent a choice worth saving;
        // everything above is the restore itself.
        _selectionRestored = true;
    }

    /// <summary>
    /// Reads the projects from disk into the picker. Fire-and-forget for the
    /// same reason the model list is: a folder read must not hold up the window
    /// appearing, and having no projects is a perfectly ordinary state.
    /// </summary>
    private async Task LoadProjectsAsync()
    {
        try
        {
            var projects = await _projectService.LoadAllAsync();

            AvailableProjects.Clear();
            AvailableProjects.Add(NoProject);

            foreach (var project in projects)
                AvailableProjects.Add(project);

            // Nothing is selected until a conversation asks for one, so a new
            // chat is not silently given permission to build somewhere.
            CurrentProject ??= NoProject;

            _logger.LogInformation("Loaded {Count} project(s)", projects.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load projects");
        }
    }

    /// <summary>
    /// The "not working in a project" entry. A real item rather than a null
    /// selection, because a combo box offers no way to pick nothing, and
    /// detaching a conversation from its project has to be possible.
    /// </summary>
    public static readonly Project NoProject = new() { Id = Guid.Empty, Name = "(no project)" };

    private static bool IsRealProject(Project? project) =>
        project != null && project.Id != Guid.Empty;

    partial void OnCurrentProjectChanged(Project? value)
    {
        // What the build and run tools read to decide which folder they may
        // touch, so it has to track the picker exactly.
        _projectContext.Current = IsRealProject(value) ? value : null;

        if (CurrentConversation != null)
            CurrentConversation.ProjectId = IsRealProject(value) ? value!.Id : null;

        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectSummary));

        if (!_selectionRestored || _applyingProjectFromConversation)
            return;

        _logger.LogInformation("Conversation is now working in project {Project} ({Root})",
            value?.Name, IsRealProject(value) ? value!.RootPath : "-");

        // The choice belongs to the conversation, so it is saved with it.
        _ = SaveConversationAsync();
    }

    public bool HasProject => IsRealProject(CurrentProject);

    /// <summary>Reads as "well_done · step-approve", shown beside the picker.</summary>
    public string ProjectSummary
    {
        get
        {
            if (!IsRealProject(CurrentProject))
                return string.Empty;

            var autonomy = CurrentProject!.Autonomy switch
            {
                AutonomyMode.FullAuto => "runs on its own",
                AutonomyMode.StepApprove => "asks before each step",
                _ => "one reply at a time"
            };

            return CurrentProject.RootExists
                ? autonomy
                : "folder is missing";
        }
    }

    /// <summary>
    /// Puts the picker on the project a loaded conversation belongs to, without
    /// that being treated as the user choosing it - which would save the
    /// conversation again for no reason.
    /// </summary>
    private void ApplyProjectFromConversation()
    {
        _applyingProjectFromConversation = true;

        try
        {
            var id = CurrentConversation?.ProjectId;

            CurrentProject = id == null
                ? NoProject
                : AvailableProjects.FirstOrDefault(p => p.Id == id) ?? NoProject;

            // A conversation pointing at a project that has since been removed
            // is worth saying out loud: its tools will silently stop working.
            if (id != null && !IsRealProject(CurrentProject))
                _logger.LogWarning("Conversation refers to project {Id}, which no longer exists", id);
        }
        finally
        {
            _applyingProjectFromConversation = false;
        }
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

        // A mode named in a hand-edited settings file that is not one of the
        // three falls back rather than leaving the picker showing nothing.
        var mode = _settingsService.GetCachedSettings().Modes.LastMode;
        _currentMode = ChatMode.All.Contains(mode) ? mode : ChatMode.Chat;

        _logger.LogInformation("Restored selection: {Provider} / {Model} / {Mode}",
            _currentProviderName,
            string.IsNullOrEmpty(_currentModel) ? "(first available)" : _currentModel,
            _currentMode);
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
                settings.Modes.LastMode = CurrentMode;
                await _settingsService.SaveSettingsAsync(settings);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not save the provider/model/mode selection");
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
        OnPropertyChanged(nameof(CanCompact));

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

    partial void OnCurrentModelChanged(string value)
    {
        PersistSelection();

        // A different model is a different window, so the same conversation can
        // go from comfortable to nearly full without a word being added.
        UpdateContextUsage();
    }

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
                _modelContextWindows[model.Id] = model.ContextWindow;
            }

            if (AvailableModels.Count > 0 && !AvailableModels.Contains(CurrentModel))
            {
                CurrentModel = AvailableModels[0];
            }

            // The window for the selected model is only known now, so the meter
            // has been measuring against the fallback until this point.
            UpdateContextUsage();

            // The window is logged because getting it wrong is invisible in the
            // UI until the meter misbehaves: every model reading 4096 was what a
            // hardcoded placeholder looked like from the outside.
            _logger.LogInformation("Loaded {Count} models from {Provider}; {Model} has a {Window}-token window",
                AvailableModels.Count, CurrentProviderName, CurrentModel, ContextWindowTokens);
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
        //
        // IsAgentRunning is checked as well as IsStreaming because between the
        // steps of an automatic run IsStreaming is false, which re-enables the
        // Send button - without this, pressing it would start a second turn
        // running concurrently with the loop's own.
        if ((string.IsNullOrWhiteSpace(CurrentInput) && !HasPendingAttachments)
            || IsStreaming || IsAgentRunning)
        {
            return;
        }

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

        // Before the model touches anything. Taking it afterwards was wrong: the
        // first turn edits files, which leaves the folder dirty, so the run then
        // refused to continue because of its own work and asked to be committed.
        var mayRunUnattended = await PrepareUnattendedRunAsync();

        await GenerateResponseAsync(typed);
        await AutoCompactIfNeededAsync();

        if (mayRunUnattended)
            await RunAutonomouslyAsync();
    }

    /// <summary>
    /// Keeps working after the first reply, when the project says to.
    ///
    /// Placed here rather than inside GenerateResponseAsync because this is the
    /// one point where everything is consistent again: IsStreaming is already
    /// false, the token source is disposed, the reply is in the conversation and
    /// saved, the meter has refreshed, and compaction has run. Re-entering
    /// earlier would race any of those.
    /// </summary>
    private async Task RunAutonomouslyAsync()
    {
        var loop = new AgentLoop(IsRealProject(CurrentProject) ? CurrentProject : null);

        if (!loop.IsAutonomous || IsAgentRunning)
            return;

        IsAgentRunning = true;

        try
        {
            while (true)
            {
                var verdict = loop.Decide(
                    completed: _signals.ConsumeComplete(),
                    cancelled: _lastTurnCancelled,
                    failed: _lastTurnFailed);

                if (verdict != LoopVerdict.Continue)
                {
                    AgentStatus = loop.Explain(verdict, _signals.Summary);
                    _logger.LogInformation("Run ended: {Verdict} after {Steps} step(s)",
                        verdict, loop.Iteration);
                    return;
                }

                if (!ShouldTakeAnotherStep(loop))
                {
                    AgentStatus = $"Stopped by you after {loop.Iteration} step(s).";
                    return;
                }

                loop.CountIteration();
                AgentStatus = $"Working on its own — step {loop.Iteration} of {loop.MaxIterations}";

                // A visible message rather than a hidden one: the transcript
                // should show why the assistant carried on.
                var nudge = new Message
                {
                    Role = MessageRole.User,
                    Content = AgentLoop.ContinuePrompt,
                    Timestamp = DateTime.UtcNow,
                    // Marked, because it has to be sent as a user turn for the
                    // model to answer it, but it did not come from the user and
                    // must not be shown as though it did.
                    IsAutoContinue = true
                };

                Messages.Add(nudge);
                CurrentConversation?.Messages.Add(nudge);

                await GenerateResponseAsync(AgentLoop.ContinuePrompt);
                await AutoCompactIfNeededAsync();
            }
        }
        finally
        {
            IsAgentRunning = false;
        }
    }

    /// <summary>
    /// Takes the way back, before any work happens, and says whether this
    /// conversation may then keep working on its own.
    ///
    /// Called ahead of the first reply rather than after it. Doing it afterwards
    /// meant the model had already edited files, so the folder was dirty and the
    /// run refused to continue on account of its own changes - which read as the
    /// assistant stopping to ask permission for no reason.
    ///
    /// A refusal does not silence the assistant. The message still gets its
    /// reply; only the unattended continuation is withheld, since that is the
    /// part with nothing to undo to.
    /// </summary>
    private async Task<bool> PrepareUnattendedRunAsync()
    {
        var loop = new AgentLoop(IsRealProject(CurrentProject) ? CurrentProject : null);

        if (!loop.IsAutonomous || IsAgentRunning)
            return false;

        // Anything left over from a previous run would otherwise stop this one
        // before it has done anything.
        _signals.Reset();

        var checkpoint = await _checkpoints.PrepareAsync(
            CurrentProject!.RootPath, CurrentProject.RequireGitCheckpoint);

        if (!checkpoint.CanProceed)
        {
            AgentStatus = "Answering once only: " + checkpoint.Message;
            _logger.LogWarning("Not working unattended: {Reason}", checkpoint.Message);

            MessageBox.Show(
                checkpoint.Message + Environment.NewLine + Environment.NewLine +
                "Your message will still be answered, but the assistant will not " +
                "carry on working by itself.",
                "Working on its own is off for now",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            return false;
        }

        _logger.LogInformation("Working on its own in {Project}: {Checkpoint}",
            CurrentProject.Name, checkpoint.Message);

        AgentStatus = checkpoint.Message;
        return true;
    }

    /// <summary>
    /// In step-approve, asks before each further step. In full automatic, does not.
    /// </summary>
    private bool ShouldTakeAnotherStep(AgentLoop loop)
    {
        if (loop.Autonomy != AutonomyMode.StepApprove)
            return true;

        var answer = MessageBox.Show(
            $"Continue working on this?{Environment.NewLine}{Environment.NewLine}" +
            $"Step {loop.Iteration + 1} of at most {loop.MaxIterations} in " +
            $"\"{CurrentProject?.Name}\".{Environment.NewLine}{Environment.NewLine}" +
            "It will keep building, running and editing files in that folder until it " +
            "reports the task finished or a limit is reached.",
            "Continue?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return answer == MessageBoxResult.Yes;
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

        // Kept with the conversation, so it survives a restart and comes back
        // when the conversation is reopened. It used to live only here, which
        // meant a conversation quietly carried on without the instructions it
        // had been started under.
        RememberTemplateOnConversation();

        _logger.LogInformation("Applied template {Name} (system prompt: {HasSystem})",
            templateName, !string.IsNullOrWhiteSpace(systemPrompt));
    }

    /// <summary>
    /// Writes the active template's prompt onto the conversation and saves it.
    /// </summary>
    private void RememberTemplateOnConversation()
    {
        if (CurrentConversation == null)
            return;

        CurrentConversation.SystemPrompt = SystemPrompt;
        CurrentConversation.TemplateName = ActiveTemplateName;

        _ = SaveConversationAsync();
    }

    [RelayCommand]
    private void ClearTemplate()
    {
        SystemPrompt = string.Empty;
        ActiveTemplateName = string.Empty;
        OnPropertyChanged(nameof(HasSystemPrompt));

        RememberTemplateOnConversation();
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

    /// <summary>
    /// Folds the older messages into a summary, by hand. Says so when there is
    /// nothing to fold, since a button that appears to do nothing is worse than
    /// one that explains itself.
    /// </summary>
    [RelayCommand]
    private async Task CompactContextAsync()
    {
        if (!await CompactAsync())
        {
            MessageBox.Show(
                "There is not enough history to compact yet. The most recent messages " +
                "are always left as they are, so a short conversation has nothing older " +
                "to summarize.",
                "Nothing to compact",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Compacts once the window is as full as the threshold, if that was asked
    /// for. Run after a reply rather than before a request, so the summarizing
    /// happens while the user is reading instead of while they are waiting.
    /// </summary>
    private async Task AutoCompactIfNeededAsync()
    {
        var context = _settingsService.GetCachedSettings().Context;

        if (!context.AutoCompact || !CanCompact)
            return;

        var window = ContextWindowTokens;
        var live = ContextMeter.LiveMessages(CurrentConversation?.Messages ?? new List<Message>());
        var usage = ContextMeter.Measure(
            live, BuildSystemPrompt(), window, _measuredPromptTokens, _measuredThrough);

        if (!usage.HasReached(context.CompactThresholdPercent))
            return;

        _logger.LogInformation(
            "Context is at {Percent}% of {Window} tokens, past the {Threshold}% threshold; compacting",
            usage.Percent, window, context.CompactThresholdPercent);

        await CompactAsync();
    }

    /// <summary>
    /// Summarizes the older messages and marks them compacted. Returns false
    /// when there was nothing to fold or the summary did not come back, in both
    /// of which cases the conversation is left exactly as it was.
    /// </summary>
    private async Task<bool> CompactAsync()
    {
        if (CurrentConversation == null || !CanCompact)
            return false;

        var keepRecent = _settingsService.GetCachedSettings().Context.KeepRecentMessages;
        var live = ContextMeter.LiveMessages(CurrentConversation.Messages);
        var toFold = ConversationCompactor.Foldable(live, keepRecent);

        if (toFold.Count == 0)
            return false;

        IsCompacting = true;
        StatusMessage = $"Compacting {toFold.Count} messages...";

        try
        {
            var summary = await _compactor.SummarizeAsync(_currentProvider, CurrentModel, toFold);

            if (summary == null)
                return false;

            // The summary goes where the folded messages ended, so the
            // conversation still reads in order with them greyed out above it.
            var firstKept = live.Count > toFold.Count ? live[toFold.Count] : null;
            var insertAt = firstKept != null
                ? CurrentConversation.Messages.IndexOf(firstKept)
                : CurrentConversation.Messages.Count;

            foreach (var message in toFold)
                message.IsCompacted = true;

            CurrentConversation.Messages.Insert(insertAt, summary);

            // Messages is what the view binds to and is kept in step with the
            // conversation, so the same insert has to happen in both.
            var viewIndex = firstKept != null ? Messages.IndexOf(firstKept) : Messages.Count;
            Messages.Insert(viewIndex < 0 ? Messages.Count : viewIndex, summary);

            // Every count the provider gave described a prompt that included the
            // messages just folded away, so none of them describes this one.
            ResetContextMeasurement();

            await SaveConversationAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compaction failed");
            MessageBox.Show($"Could not compact the conversation: {ex.Message}",
                "Compaction failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            IsCompacting = false;
            StatusMessage = string.Empty;
        }
    }

    private void StartNewConversation()
    {
        CurrentConversation = new Conversation
        {
            Title = "New Conversation",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            // Carried over from the conversation just left. Starting a new chat
            // while working on something is nearly always still working on that
            // something, and re-picking the project every time would be tedious
            // enough to be got wrong.
            ProjectId = IsRealProject(CurrentProject) ? CurrentProject!.Id : null,
            // Carried over for the same reason as the project: starting a fresh
            // chat mid-task is usually still the same task.
            SystemPrompt = SystemPrompt,
            TemplateName = ActiveTemplateName,
            Metadata = new ConversationMetadata
            {
                ModelName = CurrentModel,
                Provider = CurrentProviderName
            }
        };

        Messages.Clear();
        StreamingContent = string.Empty;
        UpdateConversationTokenSummary();
        ResetContextMeasurement();

        _logger.LogInformation("Started new conversation: {ConversationId} with provider {Provider}",
            CurrentConversation.Id, CurrentProviderName);
    }

    private async Task GenerateResponseAsync(string userInput)
    {
        IsStreaming = true;
        StreamingContent = string.Empty;
        _lastTurnCancelled = false;
        _lastTurnFailed = false;
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

            // Anything a compaction folded away is left out; the summary
            // standing in for it is not.
            var toSend = ContextMeter.LiveMessages(CurrentConversation!.Messages);
            _requestedMessageCount = toSend.Count;

            var request = new GenerateRequest
            {
                Model = CurrentModel,
                Messages = toSend,
                // These were hardcoded, so the temperature, token limit and top-p
                // in Settings were written but never sent.
                Parameters = new ModelParameters
                {
                    Temperature = defaults.Temperature,
                    MaxTokens = defaults.MaxTokens,
                    TopP = defaults.TopP
                },
                SystemPrompt = BuildSystemPrompt() is { Length: > 0 } prompt ? prompt : null,
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

                // A tool that has just run. Kept with the conversation so a later
                // turn can still see what it returned - the provider's own copy
                // is discarded when this reply finishes.
                if (chunk.ToolExchange != null)
                {
                    RecordToolExchange(chunk.ToolExchange, assistantMessage);
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
                    assistantMessage.ReasoningTokens = chunk.Metadata.ReasoningTokens;
                    assistantMessage.Cost = chunk.Metadata.Cost;

                    // The prompt count arrives with the reply but describes the
                    // input, so it belongs on the message that prompted it.
                    var prompting = Messages.LastOrDefault(m => m.Role == MessageRole.User);
                    if (prompting != null)
                        prompting.PromptTokens = chunk.Metadata.PromptEvalCount;

                    // The provider has now counted this exact prompt, so the
                    // meter can stop guessing at everything up to it. A turn
                    // that used tools sent more than these messages - the tool
                    // results went too - so the count can exceed what they
                    // account for. That reads high rather than low, which is the
                    // safe direction for a gauge of remaining room.
                    if (chunk.Metadata.PromptEvalCount is > 0)
                    {
                        _measuredPromptTokens = chunk.Metadata.PromptEvalCount;
                        _measuredThrough = _requestedMessageCount;
                    }

                    if (CurrentConversation != null)
                    {
                        CurrentConversation.Metadata.TotalPromptTokens += chunk.Metadata.PromptEvalCount ?? 0;
                        CurrentConversation.Metadata.TotalCompletionTokens += chunk.Metadata.EvalCount ?? 0;

                        if (chunk.Metadata.Cost.HasValue)
                        {
                            CurrentConversation.Metadata.TotalCost =
                                (CurrentConversation.Metadata.TotalCost ?? 0m) + chunk.Metadata.Cost.Value;
                        }

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

            // Pictures a search returned are hosted on third-party servers whose
            // URLs expire, so they are copied into the conversation folder before
            // the reply is stored. Not given the cancellation token: the answer is
            // already complete, and cancelling here would discard it.
            assistantMessage.Content = await _webImageCache.CacheImagesAsync(
                assistantMessage.Content, CurrentConversation!.Id);

            CurrentConversation!.Messages.Add(assistantMessage);
            await SaveConversationAsync();

            _logger.LogDebug("Response generation completed successfully");
        }
        catch (OperationCanceledException)
        {
            assistantMessage.Content = StreamingContent + "\n\n[Cancelled by user]";
            _lastTurnCancelled = true;
            _logger.LogInformation("Response generation was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating response");
            _lastTurnFailed = true;
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

            // In the finally so the meter also catches up after a cancelled or
            // failed turn, both of which still added messages to the history.
            UpdateContextUsage();
        }
    }

    /// <summary>
    /// Files a completed tool call into the conversation, before the reply that
    /// follows from it.
    ///
    /// The assistant's message is already in the view list but is not added to
    /// the conversation until the turn finishes, so inserting here keeps the two
    /// in the order they happened: the calls, then the answer drawn from them.
    /// </summary>
    private void RecordToolExchange(ToolExchange exchange, Message assistantMessage)
    {
        var message = new Message
        {
            Role = MessageRole.Tool,
            Content = exchange.Result,
            Timestamp = DateTime.UtcNow,
            ToolName = exchange.Name,
            ToolCallId = exchange.CallId,
            ToolArguments = exchange.Arguments
        };

        CurrentConversation?.Messages.Add(message);

        // Placed before the reply being streamed, which is the last thing in the
        // view list at this point.
        var before = Messages.IndexOf(assistantMessage);
        Messages.Insert(before < 0 ? Messages.Count : before, message);

        _logger.LogDebug("Recorded tool exchange: {Tool}", exchange.Name);
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
        ResetContextMeasurement();
        ApplyProjectFromConversation();

        // Put back the standing instruction this conversation was being held
        // under, rather than leaving whatever the previous one used.
        SystemPrompt = conversation.SystemPrompt;
        ActiveTemplateName = conversation.TemplateName;
        OnPropertyChanged(nameof(HasSystemPrompt));

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
