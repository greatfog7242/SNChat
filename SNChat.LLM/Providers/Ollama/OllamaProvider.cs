using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;
using SNChat.LLM.Providers.Base;

namespace SNChat.LLM.Providers.Ollama;

public class OllamaProvider : BaseLLMProvider
{
    protected override string BaseUrl => "http://localhost:11434";
    public override string Name => "Ollama";

    public OllamaProvider(HttpClient httpClient, ILogger<OllamaProvider> logger)
        : base(httpClient, logger)
    {
        httpClient.BaseAddress = new Uri(BaseUrl);
    }

    public override async Task<List<Model>> GetAvailableModelsAsync()
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<OllamaModelsResponse>("/api/tags");
            if (response == null || response.Models == null)
                return new List<Model>();

            return response.Models.Select(m => new Model
            {
                Id = m.Name,
                DisplayName = m.Name,
                Provider = Name,
                ParameterSize = m.Size,
                ContextWindow = 4096, // Default, Ollama doesn't always expose this
                Capabilities = new Dictionary<string, object>
                {
                    ["quantization"] = m.Details?.QuantizationLevel ?? "unknown",
                    ["parameter_size"] = m.Details?.ParameterSize ?? "unknown"
                }
            }).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get available models from Ollama");
            return new List<Model>();
        }
    }

    public override async Task<string> GenerateAsync(GenerateRequest request)
    {
        var fullResponse = new StringBuilder();

        await foreach (var chunk in GenerateStreamAsync(request))
        {
            fullResponse.Append(chunk.Content);
        }

        return fullResponse.ToString();
    }

    public override async IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request)
    {
        var ollamaRequest = ConvertToOllamaRequest(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(ollamaRequest)
        };

        HttpResponseMessage response;

        // Send the request
        try
        {
            response = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                request.CancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP error when connecting to Ollama");
            yield break; // No yield return in catch
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Request to Ollama was cancelled");
            yield break;
        }

        // Process the streaming response
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(request.CancellationToken);
            using var reader = new StreamReader(stream);

            while (!reader.EndOfStream)
            {
                if (request.CancellationToken.IsCancellationRequested)
                    yield break;

                var line = await reader.ReadLineAsync(request.CancellationToken);
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                OllamaChatResponse? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<OllamaChatResponse>(line);
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "Failed to deserialize Ollama response chunk: {Line}", line);
                    continue;
                }

                if (chunk == null)
                    continue;

                var content = chunk.Message?.Content ?? string.Empty;

                yield return new StreamChunk
                {
                    Content = content,
                    IsFinal = chunk.Done,
                    Metadata = chunk.Done ? new StreamMetadata
                    {
                        PromptEvalCount = chunk.PromptEvalCount,
                        EvalCount = chunk.EvalCount,
                        TotalDuration = chunk.TotalDuration
                    } : null
                };

                if (chunk.Done)
                    break;
            }
        }
    }

    private OllamaChatRequest ConvertToOllamaRequest(GenerateRequest request)
    {
        var messages = new List<OllamaMessage>();

        // Add system prompt if provided
        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new OllamaMessage
            {
                Role = "system",
                Content = request.SystemPrompt
            });
        }

        // Add conversation messages
        messages.AddRange(request.Messages.Select(m => new OllamaMessage
        {
            Role = m.Role.ToString().ToLowerInvariant(),
            Content = m.Content
        }));

        return new OllamaChatRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = true,
            Options = new OllamaOptions
            {
                Temperature = request.Parameters.Temperature,
                NumPredict = request.Parameters.MaxTokens,
                TopP = request.Parameters.TopP,
                FrequencyPenalty = request.Parameters.FrequencyPenalty,
                PresencePenalty = request.Parameters.PresencePenalty,
                Stop = request.Parameters.StopSequences
            }
        };
    }
}
