using SNChat.LLM.Models;

namespace SNChat.LLM.Interfaces;

public interface ILLMProvider
{
    string Name { get; }

    Task<List<Model>> GetAvailableModelsAsync();

    Task<string> GenerateAsync(GenerateRequest request);

    IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request);

    Task<bool> IsAvailableAsync();
}
