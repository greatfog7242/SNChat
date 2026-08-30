using Microsoft.Extensions.Logging;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;

namespace SNChat.LLM.Providers.Base;

public abstract class BaseLLMProvider : ILLMProvider
{
    protected readonly HttpClient HttpClient;
    protected readonly ILogger Logger;

    protected abstract string BaseUrl { get; }

    public abstract string Name { get; }

    protected BaseLLMProvider(HttpClient httpClient, ILogger logger)
    {
        HttpClient = httpClient;
        Logger = logger;
    }

    public abstract Task<List<Model>> GetAvailableModelsAsync();

    public abstract Task<string> GenerateAsync(GenerateRequest request);

    public abstract IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request);

    public virtual async Task<bool> IsAvailableAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl);
            using var response = await HttpClient.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Provider {Provider} is not available", Name);
            return false;
        }
    }
}
