using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Tools;
using SNChat.LLM.Models;
using SNChat.LLM.Providers.Base;

namespace SNChat.LLM.Providers.OpenRouter;

/// <summary>
/// Routes chats to models hosted by OpenRouter. Unlike the local Ollama
/// provider the work happens remotely, so a large model answers in seconds
/// rather than minutes - which matters most for tool use, where every round
/// trip is another full inference.
/// </summary>
public class OpenRouterProvider : BaseLLMProvider
{
    private readonly IToolRegistry? _toolRegistry;
    private readonly string _apiKey;

    protected override string BaseUrl { get; }
    public override string Name => "OpenRouter";

    public OpenRouterProvider(
        HttpClient httpClient,
        ILogger<OpenRouterProvider> logger,
        IToolRegistry? toolRegistry = null,
        string apiKey = "",
        string? baseUrl = null)
        : base(httpClient, logger)
    {
        _toolRegistry = toolRegistry;
        _apiKey = apiKey;
        BaseUrl = baseUrl ?? "https://openrouter.ai/api/v1";
        httpClient.BaseAddress = new Uri(BaseUrl.TrimEnd('/') + "/");

        if (!string.IsNullOrEmpty(_apiKey))
            httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");

        // OpenRouter attributes traffic to an app by these headers. Optional,
        // but without them requests show up as anonymous in the dashboard.
        httpClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://github.com/greatfog7242/SNChat");
        httpClient.DefaultRequestHeaders.Add("X-Title", "SNChat");
    }

    /// <summary>
    /// Lists the free, tool-capable models. OpenRouter offers ~400 models, most
    /// of them paid, which is far too many to pick from in a dropdown. Models
    /// that cannot call tools are excluded because enabling Tools with one
    /// selected fails at request time with a provider error rather than
    /// degrading gracefully.
    /// </summary>
    public override async Task<List<Model>> GetAvailableModelsAsync()
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<OpenRouterModelsResponse>("models");
            if (response?.Data == null)
                return new List<Model>();

            var models = response.Data
                .Where(IsFree)
                .Where(m => m.SupportedParameters?.Contains("tools") == true)
                .OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
                .Select(m => new Model
                {
                    Id = m.Id,
                    DisplayName = m.Id,
                    Provider = Name,
                    ContextWindow = m.ContextLength ?? 4096,
                    Capabilities = new Dictionary<string, object>
                    {
                        ["tools"] = true,
                        ["free"] = true
                    }
                })
                .ToList();

            Logger.LogInformation(
                "OpenRouter: {Shown} free tool-capable models of {Total} offered",
                models.Count, response.Data.Count);

            return models;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to get available models from OpenRouter");
            return new List<Model>();
        }
    }

    /// <summary>
    /// Prices are decimal strings per token, so "0" and "0.0000000" both mean
    /// free. Parsed invariantly - a comma-decimal locale would otherwise read
    /// "0.0000008" as a large number and treat a paid model as free.
    /// </summary>
    private static bool IsFree(OpenRouterModelInfo model)
    {
        if (model.Pricing == null)
            return false;

        return IsZero(model.Pricing.Prompt) && IsZero(model.Pricing.Completion);

        static bool IsZero(string? price) =>
            double.TryParse(price, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value == 0;
    }

    public override async Task<string> GenerateAsync(GenerateRequest request)
    {
        var fullResponse = new StringBuilder();

        await foreach (var chunk in GenerateStreamAsync(request))
            fullResponse.Append(chunk.Content);

        return fullResponse.ToString();
    }

    public override async IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request)
    {
        if (string.IsNullOrEmpty(_apiKey))
        {
            yield return new StreamChunk
            {
                Content = "⚠️ No OpenRouter API key is set. Add one under Settings → Providers.",
                IsFinal = true
            };
            yield break;
        }

        var chatRequest = ConvertToOpenRouterRequest(request);

        // Each pass is one exchange with the model. When it answers with tool
        // calls we run them, append the results, and go round again. Mirrors the
        // Ollama loop so both providers behave the same way from the UI's side.
        for (var iteration = 0; iteration <= request.MaxToolIterations; iteration++)
        {
            var pendingToolCalls = new List<OpenRouterToolCall>();
            var sawContent = false;

            await foreach (var item in SendOnceAsync(chatRequest, request.CancellationToken))
            {
                if (item.ToolCalls != null)
                {
                    pendingToolCalls.AddRange(item.ToolCalls);
                    continue;
                }

                if (item.Chunk != null)
                {
                    // Hold back the terminal chunk until we know whether tools
                    // still need to run, or the UI ends the turn early.
                    if (item.Chunk.IsFinal && pendingToolCalls.Count > 0)
                        continue;

                    if (!string.IsNullOrEmpty(item.Chunk.Content))
                        sawContent = true;

                    yield return item.Chunk;
                }
            }

            if (pendingToolCalls.Count == 0)
                yield break;

            if (_toolRegistry == null)
            {
                yield return new StreamChunk
                {
                    Content = "⚠️ The model requested a tool, but no tools are available.",
                    IsFinal = true
                };
                yield break;
            }

            if (iteration == request.MaxToolIterations)
            {
                Logger.LogWarning("Hit the {Max}-iteration tool limit", request.MaxToolIterations);
                yield return new StreamChunk
                {
                    Content = sawContent
                        ? "\n\n⚠️ Stopped after too many tool calls."
                        : "⚠️ Stopped after too many tool calls without producing an answer.",
                    IsFinal = true
                };
                yield break;
            }

            // Echo back what the model asked for, so the follow-up request keeps
            // the call/result pairing intact.
            chatRequest.Messages.Add(new OpenRouterMessage
            {
                Role = "assistant",
                Content = null,
                ToolCalls = pendingToolCalls
            });

            foreach (var call in pendingToolCalls)
            {
                var toolName = call.Function.Name;

                yield return new StreamChunk
                {
                    Content = $"🔎 Using {toolName}...",
                    IsStatus = true
                };

                var result = await _toolRegistry.ExecuteAsync(
                    new ToolCall
                    {
                        Id = call.Id ?? string.Empty,
                        Name = toolName,
                        Arguments = ParseArguments(call.Function.Arguments)
                    },
                    request.CancellationToken);

                chatRequest.Messages.Add(new OpenRouterMessage
                {
                    Role = "tool",
                    ToolCallId = call.Id,
                    Content = result.Content
                });
            }
        }
    }

    /// <summary>
    /// One request/response exchange. Emits content as it arrives and gathers
    /// any tool calls, which are only complete once the stream ends.
    /// </summary>
    private async IAsyncEnumerable<StreamItem> SendOnceAsync(
        OpenRouterChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(chatRequest)
        };

        HttpResponseMessage? response = null;
        string? errorMessage = null;

        try
        {
            response = await HttpClient.SendAsync(
                httpRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                errorMessage = ExtractError(body) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                Logger.LogError("OpenRouter request failed: {StatusCode} - {Body}", response.StatusCode, body);
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP error when connecting to OpenRouter");
            errorMessage = $"Could not reach OpenRouter. ({ex.Message})";
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Request to OpenRouter was cancelled");
            response?.Dispose();
            yield break;
        }

        if (errorMessage != null)
        {
            response?.Dispose();
            yield return new StreamItem
            {
                Chunk = new StreamChunk { Content = $"⚠️ {errorMessage}", IsFinal = true }
            };
            yield break;
        }

        using (response!)
        {
            await using var stream = await response!.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            // Tool calls stream in fragments keyed by index; they are only
            // whole once the stream finishes, so they accumulate here.
            var toolCalls = new Dictionary<int, ToolCallAccumulator>();
            var reasoning = new StringBuilder();
            var finished = false;

            while (!reader.EndOfStream)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                var line = await reader.ReadLineAsync(cancellationToken);

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                // OpenRouter sends ": OPENROUTER PROCESSING" keepalive comments
                // while a slow model warms up. They are not data.
                if (line.StartsWith(':'))
                    continue;

                if (!line.StartsWith("data: "))
                    continue;

                var data = line[6..].Trim();
                if (data == "[DONE]")
                {
                    finished = true;
                    break;
                }

                OpenRouterChatResponse? chunk;
                try
                {
                    chunk = JsonSerializer.Deserialize<OpenRouterChatResponse>(data);
                }
                catch (JsonException ex)
                {
                    Logger.LogWarning(ex, "Failed to deserialize OpenRouter chunk: {Data}", data);
                    continue;
                }

                var choice = chunk?.Choices?.FirstOrDefault();
                var delta = choice?.Delta ?? choice?.Message;
                if (delta == null)
                    continue;

                if (delta.ToolCalls != null)
                {
                    foreach (var part in delta.ToolCalls)
                    {
                        if (!toolCalls.TryGetValue(part.Index, out var acc))
                        {
                            acc = new ToolCallAccumulator();
                            toolCalls[part.Index] = acc;
                        }

                        if (!string.IsNullOrEmpty(part.Id))
                            acc.Id = part.Id;
                        if (!string.IsNullOrEmpty(part.Function?.Name))
                            acc.Name = part.Function.Name;
                        if (!string.IsNullOrEmpty(part.Function?.Arguments))
                            acc.Arguments.Append(part.Function.Arguments);
                    }
                }

                // Reasoning goes out as a status line rather than as content, so
                // it shows progress without landing in the saved answer.
                if (!string.IsNullOrEmpty(delta.Reasoning))
                {
                    reasoning.Append(delta.Reasoning);
                    yield return new StreamItem
                    {
                        Chunk = new StreamChunk
                        {
                            Content = Summarize(reasoning.ToString()),
                            IsStatus = true
                        }
                    };
                }

                if (!string.IsNullOrEmpty(delta.Content))
                {
                    yield return new StreamItem
                    {
                        Chunk = new StreamChunk { Content = delta.Content, IsFinal = false }
                    };
                }
            }

            if (toolCalls.Count > 0)
            {
                yield return new StreamItem
                {
                    ToolCalls = toolCalls
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new OpenRouterToolCall
                        {
                            Id = kv.Value.Id,
                            Function = new OpenRouterToolCallFunction
                            {
                                Name = kv.Value.Name,
                                Arguments = kv.Value.Arguments.ToString()
                            }
                        })
                        .ToList()
                };
            }

            if (finished || toolCalls.Count == 0)
            {
                yield return new StreamItem
                {
                    Chunk = new StreamChunk { Content = string.Empty, IsFinal = true }
                };
            }
        }
    }

    private class ToolCallAccumulator
    {
        public string? Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public StringBuilder Arguments { get; } = new();
    }

    private class StreamItem
    {
        public StreamChunk? Chunk { get; set; }
        public List<OpenRouterToolCall>? ToolCalls { get; set; }
    }

    /// <summary>
    /// Pulls the human-readable message out of an OpenRouter error body.
    ///
    /// When a request fails at the upstream provider rather than at OpenRouter,
    /// "message" is the useless constant "Provider returned error" and the text
    /// worth reading sits in metadata.raw - for a rate-limited free model that
    /// is the difference between no information and "retry shortly, or add your
    /// own key". Both are returned when they differ.
    /// </summary>
    private static string? ExtractError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("error", out var error))
                return null;

            if (error.ValueKind == JsonValueKind.String)
                return error.GetString();

            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;

            var detail = error.TryGetProperty("metadata", out var metadata)
                         && metadata.TryGetProperty("raw", out var raw)
                ? raw.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(detail))
                return message;

            return string.IsNullOrWhiteSpace(message) || detail.Contains(message, StringComparison.OrdinalIgnoreCase)
                ? detail
                : $"{message}: {detail}";
        }
        catch (JsonException)
        {
            // Not JSON - the caller falls back to the status code.
        }

        return null;
    }

    /// <summary>
    /// Turns the JSON-encoded argument string into plain CLR values for ITool.
    /// A model can emit malformed JSON, so a parse failure yields no arguments
    /// and lets the tool report the problem rather than tearing down the turn.
    /// </summary>
    private Dictionary<string, object?> ParseArguments(string arguments)
    {
        var result = new Dictionary<string, object?>();

        if (string.IsNullOrWhiteSpace(arguments))
            return result;

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(arguments);
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            Logger.LogWarning(ex, "Model sent malformed tool arguments: {Arguments}", arguments);
            return result;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in root.EnumerateObject())
        {
            result[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.TryGetInt64(out var l) ? l : property.Value.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => property.Value.GetRawText()
            };
        }

        return result;
    }

    /// <summary>
    /// Renders the reasoning so far as one status line. Only the tail is kept:
    /// it is the part still being written, so it keeps moving and shows the
    /// model is making progress.
    /// </summary>
    private static string Summarize(string reasoning)
    {
        const int maxLength = 90;

        var flattened = string.Join(' ', reasoning.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return flattened.Length <= maxLength
            ? flattened
            : "..." + flattened[^maxLength..];
    }

    private OpenRouterChatRequest ConvertToOpenRouterRequest(GenerateRequest request)
    {
        var messages = new List<OpenRouterMessage>();

        if (!string.IsNullOrWhiteSpace(request.SystemPrompt))
        {
            messages.Add(new OpenRouterMessage
            {
                Role = "system",
                Content = request.SystemPrompt
            });
        }

        messages.AddRange(request.Messages.Select(m => new OpenRouterMessage
        {
            Role = m.Role.ToString().ToLowerInvariant(),
            Content = m.Content
        }));

        return new OpenRouterChatRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = true,
            // Omitted entirely when no tools are enabled, so ordinary chats do
            // not pay the extra prompt tokens for tool definitions.
            Tools = request.Tools.Count > 0
                ? request.Tools.Select(ToOpenRouterTool).ToList()
                : null,
            Temperature = request.Parameters.Temperature,
            MaxTokens = request.Parameters.MaxTokens,
            TopP = request.Parameters.TopP
        };
    }

    private static OpenRouterTool ToOpenRouterTool(ITool tool) => new()
    {
        Function = new OpenRouterFunction
        {
            Name = tool.Name,
            Description = tool.Description,
            Parameters = ToolSchemaWriter.Write(tool.Parameters)
        }
    };
}
