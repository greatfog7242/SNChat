using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;
using SNChat.LLM.Providers.Base;

namespace SNChat.LLM.Providers.FreeToken;

public class FreeTokenProvider : BaseLLMProvider
{
    private readonly string _apiKey;
    protected override string BaseUrl { get; }
    public override string Name => "FreeToken";

    public FreeTokenProvider(
        HttpClient httpClient,
        ILogger<FreeTokenProvider> logger,
        string apiKey = "",
        string? baseUrl = null)
        : base(httpClient, logger)
    {
        _apiKey = apiKey;
        BaseUrl = baseUrl ?? "https://api.freetoken.ai/v1";
        httpClient.BaseAddress = new Uri(BaseUrl);

        if (!string.IsNullOrEmpty(_apiKey))
        {
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }
    }

    public override async Task<List<Model>> GetAvailableModelsAsync()
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<FreeTokenModelsResponse>("/models");
            if (response == null || response.Data == null)
                return new List<Model>();

            return response.Data.Select(m => new Model
            {
                Id = m.Id,
                DisplayName = m.Id,
                Provider = Name,
                ContextWindow = GetContextWindowForModel(m.Id),
                Capabilities = new Dictionary<string, object>
                {
                    ["owned_by"] = m.OwnedBy
                }
            }).ToList();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get available models from FreeToken");
            return GetDefaultModels();
        }
    }

    private List<Model> GetDefaultModels()
    {
        return new List<Model>
        {
            new Model { Id = "gpt-3.5-turbo", DisplayName = "GPT-3.5 Turbo", Provider = Name, ContextWindow = 4096 },
            new Model { Id = "gpt-4", DisplayName = "GPT-4", Provider = Name, ContextWindow = 8192 },
            new Model { Id = "gpt-4-turbo", DisplayName = "GPT-4 Turbo", Provider = Name, ContextWindow = 128000 },
            new Model { Id = "claude-3-haiku", DisplayName = "Claude 3 Haiku", Provider = Name, ContextWindow = 200000 },
            new Model { Id = "claude-3-sonnet", DisplayName = "Claude 3 Sonnet", Provider = Name, ContextWindow = 200000 },
            new Model { Id = "claude-3-opus", DisplayName = "Claude 3 Opus", Provider = Name, ContextWindow = 200000 },
        };
    }

    private int GetContextWindowForModel(string modelId)
    {
        return modelId.ToLower() switch
        {
            var m when m.Contains("gpt-4-turbo") => 128000,
            var m when m.Contains("gpt-4") => 8192,
            var m when m.Contains("gpt-3.5") => 4096,
            var m when m.Contains("claude-3") => 200000,
            var m when m.Contains("gemini") => 32000,
            _ => 4096
        };
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
        var freeTokenRequest = ConvertToFreeTokenRequest(request);
        var jsonContent = JsonSerializer.Serialize(freeTokenRequest);
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/chat/completions")
        {
            Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
        };

        HttpResponseMessage? response = null;
        bool errorOccurred = false;
        string errorMessage = string.Empty;

        try
        {
            response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP error when connecting to FreeToken");
            errorOccurred = true;
            errorMessage = ex.Message;
        }

        if (errorOccurred)
        {
            yield return new StreamChunk
            {
                Content = $"Error: {errorMessage}",
                IsFinal = true
            };
            yield break;
        }

        if (response == null)
            yield break;

        try
        {
            if (freeTokenRequest.Stream)
            {
                await foreach (var chunk in ProcessStreamingResponseAsync(response))
                {
                    yield return chunk;
                }
            }
            else
            {
                var result = await response.Content.ReadFromJsonAsync<FreeTokenChatResponse>();
                if (result?.Choices != null && result.Choices.Count > 0)
                {
                    var content = result.Choices[0].Message?.Content ?? string.Empty;
                    yield return new StreamChunk
                    {
                        Content = content,
                        IsFinal = true,
                        Metadata = result.Usage != null ? new StreamMetadata
                        {
                            PromptEvalCount = result.Usage.PromptTokens,
                            EvalCount = result.Usage.CompletionTokens
                        } : null
                    };
                }
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    private async IAsyncEnumerable<StreamChunk> ProcessStreamingResponseAsync(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream);

        var totalPromptTokens = 0;
        var totalCompletionTokens = 0;

        // Reads until ReadLineAsync returns null rather than testing
        // EndOfStream, which is synchronous and blocks on the socket. This
        // iterator resumes on the UI thread, so that block froze the window for
        // as long as the model paused between tokens.
        while (true)
        {
            var line = await reader.ReadLineAsync().ConfigureAwait(false);
            if (line == null)
                break;

            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data: "))
                continue;

            var data = line.Substring(6).Trim();
            if (data == "[DONE]")
            {
                yield return new StreamChunk
                {
                    Content = string.Empty,
                    IsFinal = true,
                    Metadata = new StreamMetadata
                    {
                        PromptEvalCount = totalPromptTokens > 0 ? totalPromptTokens : null,
                        EvalCount = totalCompletionTokens > 0 ? totalCompletionTokens : null
                    }
                };
                break;
            }

            FreeTokenChatResponse? chunk = null;
            try
            {
                chunk = JsonSerializer.Deserialize<FreeTokenChatResponse>(data);
            }
            catch (JsonException ex)
            {
                Logger.LogWarning(ex, "Failed to parse streaming chunk: {Data}", data);
                continue;
            }

            if (chunk?.Choices != null && chunk.Choices.Count > 0)
            {
                var delta = chunk.Choices[0].Delta;
                var content = delta?.Content ?? string.Empty;

                if (chunk.Usage != null)
                {
                    totalPromptTokens = chunk.Usage.PromptTokens;
                    totalCompletionTokens = chunk.Usage.CompletionTokens;
                }

                if (!string.IsNullOrEmpty(content) || chunk.Choices[0].FinishReason != null)
                {
                    yield return new StreamChunk
                    {
                        Content = content,
                        IsFinal = chunk.Choices[0].FinishReason != null
                    };
                }
            }
        }
    }

    private FreeTokenChatRequest ConvertToFreeTokenRequest(GenerateRequest request)
    {
        return new FreeTokenChatRequest
        {
            Model = request.Model,
            Messages = request.Messages.Select(m => new FreeTokenMessage
            {
                Role = m.Role.ToString().ToLower(),
                Content = m.Content
            }).ToList(),
            Temperature = request.Parameters?.Temperature,
            MaxTokens = request.Parameters?.MaxTokens,
            TopP = request.Parameters?.TopP,
            Stream = true
        };
    }
}
