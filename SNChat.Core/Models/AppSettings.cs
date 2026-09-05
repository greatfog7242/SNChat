namespace SNChat.Core.Models;

public class AppSettings
{
    public ProviderSettings Providers { get; set; } = new();
    public DefaultParameters Defaults { get; set; } = new();
    public ToolSettings Tools { get; set; } = new();
    public UIPreferences UI { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public ModeSettings Modes { get; set; } = new();
    public ContextSettings Context { get; set; } = new();
}

/// <summary>
/// When to fold the older part of a conversation into a summary. Every turn
/// resends the whole history, so a long conversation eventually fills the
/// model's context window - at which point the request stops being merely
/// expensive and starts being rejected outright. Compacting trades the exact
/// wording of early messages for room to keep going.
/// </summary>
public class ContextSettings
{
    /// <summary>
    /// Compact on its own once <see cref="CompactThresholdPercent"/> is reached,
    /// rather than waiting to be asked. The meter and the Compact button stay
    /// available either way, so turning this off means manual-only.
    /// </summary>
    public bool AutoCompact { get; set; } = true;

    /// <summary>
    /// How full the window has to get before compacting, as a percentage.
    /// Deliberately short of 100: the reply has to fit as well as the prompt,
    /// and compacting itself needs room to send what it is summarizing.
    /// </summary>
    public int CompactThresholdPercent { get; set; } = 80;

    /// <summary>
    /// How many of the most recent messages are left untouched. Compacting
    /// everything would summarize away the exchange in progress, which is the
    /// part the next answer depends on most.
    /// </summary>
    public int KeepRecentMessages { get; set; } = 6;

    /// <summary>
    /// Window size assumed for a model whose provider does not report one.
    /// Ollama is the usual case: its model list carries no context length, so
    /// without a figure here the meter would have nothing to measure against.
    /// </summary>
    public int FallbackWindowTokens { get; set; } = 8192;
}

/// <summary>The answering styles offered in the main window's Mode picker.</summary>
public static class ChatMode
{
    public const string Chat = "Chat";
    public const string Coding = "Coding";
    public const string Scientific = "Scientific";

    public static readonly string[] All = { Chat, Coding, Scientific };
}

/// <summary>
/// A standing instruction per mode, sent ahead of the conversation. Held as a
/// system prompt rather than pasted onto each message so that switching mode
/// changes how the next answer comes back without leaving the old instruction
/// buried in turns already saved.
/// </summary>
public class ModeSettings
{
    /// <summary>
    /// Which mode was in use when the app last ran. Kept here beside the
    /// prompts, matching how the last provider and model are remembered.
    /// </summary>
    public string LastMode { get; set; } = ChatMode.Chat;

    public string ChatPrompt { get; set; } =
        "Answer plainly and conversationally. Get to the point, and say when " +
        "you are unsure rather than guessing with confidence.";

    public string CodingPrompt { get; set; } =
        "You are helping with software. Prefer complete, runnable code over " +
        "fragments, and match the conventions of the code you are shown. " +
        "Point out edge cases, error handling and anything that looks like a " +
        "bug. When you are uncertain whether an API exists, say so rather " +
        "than inventing one.";

    public string ScientificPrompt { get; set; } =
        "Answer with precision. Carry units through calculations and state " +
        "the assumptions a result depends on. Separate what is established " +
        "from what is contested or speculative, and give the reasoning rather " +
        "than only the conclusion.";

    /// <summary>
    /// The prompt for a mode, or empty for an unknown one, so a hand-edited
    /// settings file naming a mode that does not exist simply adds nothing.
    /// </summary>
    public string PromptFor(string mode) => mode switch
    {
        ChatMode.Chat => ChatPrompt,
        ChatMode.Coding => CodingPrompt,
        ChatMode.Scientific => ScientificPrompt,
        _ => string.Empty
    };

    /// <summary>
    /// The whole system prompt for a request: this mode's standing instruction
    /// first, then whatever a template set, separated by a blank line. Either
    /// may be absent - a mode can be blanked out in Settings, and most
    /// templates carry no system prompt - and an empty result means no system
    /// message should be sent at all rather than an empty one.
    /// </summary>
    public string BuildSystemPrompt(string mode, string? templatePrompt)
    {
        var parts = new[] { PromptFor(mode), templatePrompt }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim());

        return string.Join("\n\n", parts);
    }
}

public class ProviderSettings
{
    /// <summary>
    /// Where Ollama is listening. Points at this machine by default, but an
    /// instance shared over a network is addressed here - the serving machine
    /// must be started with OLLAMA_HOST=0.0.0.0 to accept anything other than
    /// its own loopback.
    /// </summary>
    public string OllamaBaseUrl { get; set; } = "http://localhost:11434";

    /// <summary>
    /// How much context Ollama should serve a model with, sent as num_ctx.
    ///
    /// Zero, the default, sends nothing and lets Ollama size it as it sees fit -
    /// which is what it did before this existed. The catch is that nothing then
    /// reports what it settled on: the context meter has to fall back to the
    /// model's own trained length, which is an upper bound and can be far above
    /// what is actually being served.
    ///
    /// Setting it makes both ends agree - the request asks for exactly this much
    /// and the meter measures against exactly this much. It costs memory though:
    /// the KV cache grows with this number, so too large a value pushes layers
    /// off the GPU and slows generation badly, or fails to load at all.
    ///
    /// Capped at the model's trained length when that is known, since asking for
    /// more than a model was built for does not give it a longer memory.
    /// </summary>
    public int OllamaContextWindow { get; set; }

    public string FreeTokenApiKey { get; set; } = string.Empty;
    public string FreeTokenBaseUrl { get; set; } = "https://api.freetoken.ai/v1";
    public string OpenRouterApiKey { get; set; } = string.Empty;
    public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>
    /// Model-id prefixes whose traffic should be pinned to one upstream
    /// provider, for providers you have given OpenRouter your own key for
    /// (Settings -> Integrations on openrouter.ai).
    ///
    /// Without pinning, a request that your own key cannot serve is quietly
    /// retried against a different provider and billed to OpenRouter credits
    /// instead of your quota. Pinning turns that into a visible error.
    ///
    /// Never applied to ":free" models: those are the shared-pool variants that
    /// your own key has nothing to do with. Add entries by hand as you add keys.
    /// </summary>
    public Dictionary<string, string> OpenRouterByokProviders { get; set; } = new()
    {
        ["google/"] = "google-ai-studio"
    };

    /// <summary>
    /// Model ids to offer in the main window's dropdown. OpenRouter carries
    /// several hundred tool-capable models, which is unusable as a flat list,
    /// so the picker in Settings narrows it to the handful actually used.
    /// Empty means offer all of them.
    /// </summary>
    public List<string> OpenRouterSelectedModels { get; set; } = new();
    public string AnthropicApiKey { get; set; } = string.Empty;
    public string OpenAIApiKey { get; set; } = string.Empty;

    /// <summary>Google Cloud API key for the Custom Search JSON API.</summary>
    public string GoogleApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Programmable Search Engine ID ("cx"). The engine must have
    /// "Search the entire web" and image search enabled, or results are empty.
    /// </summary>
    public string GoogleSearchEngineId { get; set; } = string.Empty;
}

public static class ImageSourcePreference
{
    /// <summary>Use Google when configured, otherwise Wikimedia Commons.</summary>
    public const string Auto = "Auto";
    public const string Google = "Google";
    public const string Commons = "Wikimedia Commons";

    public static readonly string[] All = { Auto, Google, Commons };
}

public class DefaultParameters
{
    public double Temperature { get; set; } = 0.7;
    /// <summary>
    /// Generous because reasoning models spend this budget thinking before they
    /// write anything: at 2048 one can hit the limit mid-thought and return an
    /// answer of nothing at all, having looked like it worked the whole time.
    /// </summary>
    public int MaxTokens { get; set; } = 8192;
    public double TopP { get; set; } = 0.9;

    /// <summary>Used on first run, before anything has been picked.</summary>
    public string DefaultProvider { get; set; } = "Ollama";
    public string DefaultModel { get; set; } = string.Empty;

    /// <summary>
    /// Provider and model in use when the app last ran, restored on launch so a
    /// session picks up where the previous one left off. Kept apart from
    /// DefaultProvider so that switching provider in the main window does not
    /// silently rewrite the default configured in Settings. Empty until
    /// something has been selected, in which case the defaults above apply.
    /// </summary>
    public string LastProvider { get; set; } = string.Empty;
    public string LastModel { get; set; } = string.Empty;
}

public class ToolSettings
{
    /// <summary>
    /// Whether the Tools switch starts on. Leaving it on means the model can
    /// search without the switch having to be found first, at the cost of
    /// sending every tool definition with each request: that is a larger prompt
    /// on every turn, which a local model pays for in noticeably slower replies.
    /// Turn it off here to go back to opting in per session.
    /// </summary>
    public bool EnabledByDefault { get; set; } = true;

    /// <summary>One of <see cref="ImageSourcePreference"/>.</summary>
    public string ImageSource { get; set; } = ImageSourcePreference.Auto;

    /// <summary>
    /// Falls back to Wikimedia Commons when the chosen source returns nothing,
    /// which also covers running out of Google's daily quota.
    /// </summary>
    public bool FallbackToCommons { get; set; } = true;

    /// <summary>One of <see cref="WebSourcePreference"/>.</summary>
    public string WebSource { get; set; } = WebSourcePreference.Auto;

    /// <summary>
    /// Applies Google's SafeSearch filter to both web and image results.
    /// Has no effect on the DuckDuckGo or Wikipedia lookups.
    /// </summary>
    public bool SafeSearch { get; set; } = true;

    /// <summary>
    /// MCP (Model Context Protocol) servers to connect to.
    /// Each server provides tools that the LLM can use.
    /// </summary>
    public List<McpServerConfig> McpServers { get; set; } = new();

    /// <summary>
    /// How many rounds of tool calls the model may make before it has to answer.
    /// Raise it for research that needs several searches to narrow down; lower
    /// it to fail faster when each round is expensive, as with a large local
    /// model. A round can contain more than one call, so the real number of
    /// calls allowed is higher than this.
    /// </summary>
    public int MaxToolIterations { get; set; } = 10;
}

/// <summary>Configuration for an MCP server.</summary>
public class McpServerConfig
{
    /// <summary>Friendly name for this server (for logging/UI).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Command to execute (e.g., "npx", "python", "node").</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments to pass to the command.</summary>
    public string Arguments { get; set; } = string.Empty;

    /// <summary>
    /// Extra environment variables for the server process, added to the ones it
    /// inherits. Many servers take their endpoint or credentials this way rather
    /// than on the command line - mcp-searxng reads SEARXNG_URL, and API-backed
    /// servers read their key - so without this they cannot be configured at all.
    /// </summary>
    public Dictionary<string, string> Env { get; set; } = new();

    /// <summary>Whether this server should be started automatically.</summary>
    public bool Enabled { get; set; } = true;
}

public static class WebSourcePreference
{
    /// <summary>Use Google when configured, otherwise DuckDuckGo + Wikipedia.</summary>
    public const string Auto = "Auto";
    public const string Google = "Google";
    public const string Encyclopedic = "DuckDuckGo + Wikipedia";

    public static readonly string[] All = { Auto, Google, Encyclopedic };
}

public class UIPreferences
{
    public string Theme { get; set; } = "Light";
    public int FontSize { get; set; } = 14;
    public bool ShowTimestamps { get; set; } = true;
    public bool EnableMarkdown { get; set; } = true;
    public int SidebarWidth { get; set; } = 300;
}

public class StorageSettings
{
    public string ConversationsPath { get; set; } = string.Empty; // Empty means use default
    public bool AutoSave { get; set; } = true;
    public int MaxConversationsToKeep { get; set; } = 1000;
}
