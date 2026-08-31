namespace SNChat.LLM.Providers.OpenRouter;

/// <summary>
/// The parts of OpenRouter's configuration that can change while the app is
/// running. Read per request rather than captured in the constructor, because
/// the provider is a singleton built at startup: capturing them meant a key or
/// model selection saved in Settings did nothing until the app was relaunched.
/// </summary>
public sealed class OpenRouterRuntimeOptions
{
    public string ApiKey { get; init; } = string.Empty;

    /// <summary>Model-id prefix to upstream provider slug, for BYOK pinning.</summary>
    public IReadOnlyDictionary<string, string> ByokProviders { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// Model ids to offer in the main dropdown. Empty means offer everything,
    /// which is the state before the user has picked any.
    /// </summary>
    public IReadOnlyList<string> SelectedModels { get; init; } = Array.Empty<string>();
}
