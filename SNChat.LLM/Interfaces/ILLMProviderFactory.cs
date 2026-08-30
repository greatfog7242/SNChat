namespace SNChat.LLM.Interfaces;

public interface ILLMProviderFactory
{
    ILLMProvider GetProvider(string providerName);
    IEnumerable<string> GetAvailableProviders();
    void RegisterProvider(string name, ILLMProvider provider);
}
