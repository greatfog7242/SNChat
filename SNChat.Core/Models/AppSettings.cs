namespace SNChat.Core.Models;

public class AppSettings
{
    public ProviderSettings Providers { get; set; } = new();
    public DefaultParameters Defaults { get; set; } = new();
    public ToolSettings Tools { get; set; } = new();
    public UIPreferences UI { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
}

public class ProviderSettings
{
    public string FreeTokenApiKey { get; set; } = string.Empty;
    public string FreeTokenBaseUrl { get; set; } = "https://api.freetoken.ai/v1";
    public string OpenRouterApiKey { get; set; } = string.Empty;
    public string OpenRouterBaseUrl { get; set; } = "https://openrouter.ai/api/v1";
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
    public int MaxTokens { get; set; } = 2048;
    public double TopP { get; set; } = 0.9;
    public string DefaultProvider { get; set; } = "Ollama";
    public string DefaultModel { get; set; } = string.Empty;
}

public class ToolSettings
{
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
