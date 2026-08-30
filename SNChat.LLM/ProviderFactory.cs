using SNChat.LLM.Interfaces;

namespace SNChat.LLM;

public class ProviderFactory : ILLMProviderFactory
{
    private readonly Dictionary<string, ILLMProvider> _providers = new();

    public void RegisterProvider(string name, ILLMProvider provider)
    {
        _providers[name] = provider;
    }

    public ILLMProvider GetProvider(string providerName)
    {
        if (_providers.TryGetValue(providerName, out var provider))
        {
            return provider;
        }

        throw new ArgumentException($"Provider '{providerName}' is not registered.", nameof(providerName));
    }

    public IEnumerable<string> GetAvailableProviders()
    {
        return _providers.Keys;
    }
}
