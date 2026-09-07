using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Tools;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;
using SNChat.LLM.Providers.Base;

namespace SNChat.LLM.Providers.Ollama;

public class OllamaProvider : BaseLLMProvider
{
    private readonly IToolRegistry? _toolRegistry;

    /// <summary>
    /// Configurable because Ollama is not always on the machine running this:
    /// one host with a capable GPU commonly serves several clients over a
    /// network. Fixed at construction, as it sets the HttpClient's address.
    /// </summary>
    protected override string BaseUrl { get; }

    public override string Name => "Ollama";

    /// <summary>
    /// The configured num_ctx, read per request rather than captured, because
    /// this provider is a singleton built at startup - capturing it would mean a
    /// change in Settings did nothing until the app was relaunched. Returns 0
    /// when unset, which leaves Ollama to size the context itself.
    /// </summary>
    private readonly Func<int> _configuredContextWindow;

    public OllamaProvider(
        HttpClient httpClient,
        ILogger<OllamaProvider> logger,
        IToolRegistry? toolRegistry = null,
        string? baseUrl = null,
        Func<int>? configuredContextWindow = null)
        : base(httpClient, logger)
    {
        BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl.TrimEnd('/');
        httpClient.BaseAddress = new Uri(BaseUrl);
        _toolRegistry = toolRegistry;
        _configuredContextWindow = configuredContextWindow ?? (() => 0);
    }

    /// <summary>
    /// The window a request will actually get: what was configured, but never
    /// more than the model was trained for, since asking beyond that does not
    /// buy a longer memory. Falls back to the model's own length when nothing is
    /// configured, and to 0 when neither is known.
    /// </summary>
    public static int EffectiveContextWindow(int configured, long modelLength)
    {
        if (configured <= 0)
            return modelLength > 0 ? (int)Math.Min(modelLength, int.MaxValue) : 0;

        return modelLength > 0
            ? (int)Math.Min(configured, modelLength)
            : configured;
    }

    public override async Task<List<Model>> GetAvailableModelsAsync()
    {
        try
        {
            var response = await HttpClient.GetFromJsonAsync<OllamaModelsResponse>("/api/tags");
            if (response == null || response.Models == null)
                return new List<Model>();

            // /api/tags carries no context length, so each model is asked for its
            // own. In parallel because this is one round trip per installed
            // model, and serially that is a visible pause on every provider switch.
            var contextLengths = await Task.WhenAll(
                response.Models.Select(async m => (m.Name, Length: await GetContextLengthAsync(m.Name))));

            var byModel = contextLengths.ToDictionary(x => x.Name, x => x.Length);
            var configured = _configuredContextWindow();

            return response.Models.Select(m => new Model
            {
                Id = m.Name,
                DisplayName = m.Name,
                Provider = Name,
                ParameterSize = m.Size,
                // What a request will really be served with, so the context
                // meter measures against the same figure the request asks for.
                ContextWindow = EffectiveContextWindow(
                    configured, byModel.TryGetValue(m.Name, out var length) ? length : 0),
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

    /// <summary>
    /// How many tokens the model can hold, from /api/show, or 0 when that cannot
    /// be established - which leaves the caller to fall back rather than act on a
    /// number nobody reported.
    ///
    /// This is the model's own context length. What Ollama actually serves is
    /// num_ctx, which it sizes to fit available memory and so can be lower on a
    /// small GPU. Reporting the model's figure therefore reads generously - but
    /// the previous hardcoded 4096 was wrong by 64x on a 256k model, which
    /// pushed the context meter to 100% on the first message and triggered a
    /// compaction that was not needed.
    /// </summary>
    private async Task<long> GetContextLengthAsync(string model)
    {
        try
        {
            using var response = await HttpClient.PostAsJsonAsync("/api/show", new { model });

            if (!response.IsSuccessStatusCode)
            {
                Logger.LogDebug("Ollama /api/show returned {Status} for {Model}",
                    response.StatusCode, model);
                return 0;
            }

            var show = await response.Content.ReadFromJsonAsync<OllamaShowResponse>();
            var length = show?.ContextLength ?? 0;

            if (length == 0)
                Logger.LogDebug("Ollama reported no context length for {Model}", model);

            return length;
        }
        catch (Exception ex)
        {
            // Not an error worth failing the model list over: an unknown window
            // only costs the accuracy of the meter.
            Logger.LogDebug(ex, "Could not read the context length for {Model}", model);
            return 0;
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

    /// <summary>
    /// Pulls the human-readable message out of an Ollama error body such as
    /// {"error":"model 'llama3.1:8b' not found"}. Returns null if absent.
    /// </summary>
    private static string? ExtractOllamaError(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.GetString();
        }
        catch (JsonException)
        {
            // Not JSON - fall through and let the caller use the status code.
        }

        return null;
    }

    public override async IAsyncEnumerable<StreamChunk> GenerateStreamAsync(GenerateRequest request)
    {
        var ollamaRequest = ConvertToOllamaRequest(request);

        // Each pass is one exchange with the model. When the model answers with
        // tool calls we run them, append the results, and go round again. The
        // iteration cap stops a model that keeps calling tools forever.
        for (var iteration = 0; iteration <= request.MaxToolIterations; iteration++)
        {
            var pendingToolCalls = new List<OllamaToolCall>();
            var sawContent = false;

            await foreach (var item in SendOnceAsync(ollamaRequest, request.CancellationToken))
            {
                if (item.ToolCall != null)
                {
                    pendingToolCalls.Add(item.ToolCall);
                    continue;
                }

                if (item.Chunk != null)
                {
                    // Hold back the terminal chunk until we know whether tools
                    // still need to run; otherwise the UI would end the turn early.
                    if (item.Chunk.IsFinal && pendingToolCalls.Count > 0)
                        continue;

                    if (!string.IsNullOrEmpty(item.Chunk.Content))
                        sawContent = true;

                    yield return item.Chunk;
                }
            }

            if (pendingToolCalls.Count == 0)
            {
                // Plain answer, already streamed above.
                yield break;
            }

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

            // Record what the model asked for, so the follow-up request keeps
            // the call/result pairing intact.
            ollamaRequest.Messages.Add(new OllamaMessage
            {
                Role = "assistant",
                Content = string.Empty,
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
                        Arguments = UnpackArguments(call.Function.Arguments)
                    },
                    request.CancellationToken);

                ollamaRequest.Messages.Add(new OllamaMessage
                {
                    Role = "tool",
                    ToolName = toolName,
                    Content = result.Content
                });

                // Handed up so the app can keep it with the conversation. The
                // copy above lives only for this request.
                yield return new StreamChunk
                {
                    ToolExchange = new ToolExchange
                    {
                        Name = toolName,
                        CallId = call.Id ?? string.Empty,
                        Arguments = call.Function.Arguments.ValueKind == JsonValueKind.Undefined
                            ? "{}"
                            : call.Function.Arguments.GetRawText(),
                        Result = result.Content
                    }
                };
            }
        }
    }

    /// <summary>
    /// One request/response exchange. Emits content chunks as they arrive and
    /// surfaces any tool calls the model asked for.
    /// </summary>
    private async IAsyncEnumerable<StreamItem> SendOnceAsync(
        OllamaChatRequest ollamaRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "/api/chat")
        {
            Content = JsonContent.Create(ollamaRequest)
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
                // Ollama returns a JSON body explaining the failure, e.g.
                // {"error":"model 'llama3.1:8b' not found"} with a 404.
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                errorMessage = ExtractOllamaError(body) ?? $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
                Logger.LogError("Ollama request failed: {StatusCode} - {Body}", response.StatusCode, body);
            }
        }
        catch (HttpRequestException ex)
        {
            Logger.LogError(ex, "HTTP error when connecting to Ollama");
            errorMessage = $"Could not reach Ollama at {BaseUrl}. Is it running? ({ex.Message})";
        }
        catch (TaskCanceledException)
        {
            Logger.LogWarning("Request to Ollama was cancelled");
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

            // Thinking arrives a few characters at a time. Showing each fragment
            // on its own would just flicker, so it accumulates here and the
            // status line shows the tail of the reasoning so far.
            var thinking = new StringBuilder();

            // Reads until ReadLineAsync returns null rather than testing
            // EndOfStream, which is synchronous: it blocks on the socket to
            // decide whether more data exists. This iterator resumes on the UI
            // thread, so that block froze the whole window for as long as the
            // model went without emitting a token - minutes, for a reasoning
            // model that thinks before it answers.
            while (true)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    break;

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

                if (chunk.Message?.ToolCalls is { Count: > 0 } toolCalls)
                {
                    foreach (var call in toolCalls)
                        yield return new StreamItem { ToolCall = call };
                }

                // Reasoning goes out as a status line rather than as content, so
                // it shows the model is working without landing in the saved
                // answer. Emitted separately because a chunk carrying thinking
                // usually carries no content at all.
                if (!string.IsNullOrEmpty(chunk.Message?.Thinking))
                {
                    thinking.Append(chunk.Message.Thinking);
                    yield return new StreamItem
                    {
                        Chunk = new StreamChunk
                        {
                            Content = Summarize(thinking.ToString()),
                            IsStatus = true
                        }
                    };
                }

                yield return new StreamItem
                {
                    Chunk = new StreamChunk
                    {
                        Content = chunk.Message?.Content ?? string.Empty,
                        IsFinal = chunk.Done,
                        Metadata = chunk.Done ? new StreamMetadata
                        {
                            PromptEvalCount = chunk.PromptEvalCount,
                            EvalCount = chunk.EvalCount,
                            TotalDuration = chunk.TotalDuration
                        } : null
                    }
                };

                if (chunk.Done)
                    break;
            }
        }
    }

    /// <summary>
    /// Flattens Ollama's JSON argument object into plain CLR values for ITool.
    /// </summary>
    private static Dictionary<string, object?> UnpackArguments(JsonElement arguments) =>
        ToolArgumentReader.Read(arguments);

    /// <summary>
    /// Stored arguments back as JSON. An empty object when they are missing or
    /// unparseable, since a malformed call would be rejected outright and losing
    /// the arguments of one historical call is better than losing the request.
    /// </summary>
    private static JsonElement ParseArgumentsElement(string arguments)
    {
        if (!string.IsNullOrWhiteSpace(arguments))
        {
            try
            {
                return JsonDocument.Parse(arguments).RootElement.Clone();
            }
            catch (JsonException)
            {
            }
        }

        return JsonDocument.Parse("{}").RootElement.Clone();
    }

    private class StreamItem
    {
        public StreamChunk? Chunk { get; set; }
        public OllamaToolCall? ToolCall { get; set; }
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

        foreach (var message in request.Messages)
        {
            // A stored tool exchange becomes the two messages it originally was:
            // the assistant's call, then the result. Ollama pairs them by tool
            // name, so no id is needed on the way back out.
            if (message.IsToolExchange)
            {
                messages.Add(new OllamaMessage
                {
                    Role = "assistant",
                    Content = string.Empty,
                    ToolCalls = new List<OllamaToolCall>
                    {
                        new()
                        {
                            Function = new OllamaToolCallFunction
                            {
                                Name = message.ToolName,
                                Arguments = ParseArgumentsElement(message.ToolArguments)
                            }
                        }
                    }
                });

                messages.Add(new OllamaMessage
                {
                    Role = "tool",
                    ToolName = message.ToolName,
                    Content = message.Content
                });

                continue;
            }

            messages.Add(new OllamaMessage
            {
                Role = message.Role.ToString().ToLowerInvariant(),
                Content = message.Content,
                Images = EncodeImages(message)
            });
        }

        return new OllamaChatRequest
        {
            Model = request.Model,
            Messages = messages,
            Stream = true,
            // Omitted entirely when no tools are enabled, so ordinary chats do
            // not pay the extra prompt tokens for tool definitions.
            Tools = request.Tools.Count > 0
                ? request.Tools.Select(ToOllamaTool).ToList()
                : null,
            Options = new OllamaOptions
            {
                Temperature = request.Parameters.Temperature,
                NumPredict = request.Parameters.MaxTokens,
                // Left out when unset so Ollama keeps choosing for itself; the
                // model's own cap is not applied here because that would need a
                // second round trip per request just to look it up.
                NumCtx = _configuredContextWindow() is > 0 and var numCtx ? numCtx : null,
                TopP = request.Parameters.TopP,
                FrequencyPenalty = request.Parameters.FrequencyPenalty,
                PresencePenalty = request.Parameters.PresencePenalty,
                Stop = request.Parameters.StopSequences
            }
        };
    }

    /// <summary>
    /// Reads any image attachments and base64-encodes them for vision models.
    /// Returns null when the message has none, so the field is omitted entirely
    /// rather than sent as an empty array.
    /// </summary>
    /// <summary>
    /// Renders the reasoning so far as one status line. Newlines are flattened
    /// because the status area is a single line, and only the tail is kept: it
    /// is the part still being written, so it keeps moving and shows the model
    /// is making progress rather than freezing on the opening words.
    /// </summary>
    private static string Summarize(string thinking)
    {
        const int maxLength = 90;

        var flattened = string.Join(' ', thinking.Split(
            new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        if (flattened.Length <= maxLength)
            return $"Thinking: {flattened}";

        return $"Thinking: ...{flattened[^maxLength..].TrimStart()}";
    }

    private List<string>? EncodeImages(Message message)
    {
        if (message.Attachments.Count == 0)
            return null;

        List<string>? encoded = null;

        foreach (var attachment in message.Attachments)
        {
            if (attachment.Type != AttachmentType.Image)
                continue;

            // SVG is vector text, not a raster image a vision model can consume.
            if (attachment.MimeType == "image/svg+xml")
            {
                Logger.LogInformation("Skipping SVG attachment {Name}; not a raster image",
                    attachment.FileName);
                continue;
            }

            try
            {
                var bytes = File.ReadAllBytes(attachment.ImagePathForModel);
                (encoded ??= new List<string>()).Add(Convert.ToBase64String(bytes));
            }
            catch (Exception ex)
            {
                // A missing or unreadable file should not sink the whole request.
                Logger.LogWarning(ex, "Could not read image attachment {Path}",
                    attachment.FilePath);
            }
        }

        if (encoded != null)
            Logger.LogInformation("Attached {Count} image(s) to the request", encoded.Count);

        return encoded;
    }

    private static OllamaTool ToOllamaTool(ITool tool) => new()
    {
        Function = new OllamaFunction
        {
            Name = tool.Name,
            Description = tool.Description,
            Parameters = ToolSchemaWriter.Write(tool.Parameters)
        }
    };
}
