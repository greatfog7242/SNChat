using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SNChat.Core.Models;
using SNChat.Core.Tools;
using SNChat.LLM.Models;
using SNChat.LLM.Providers.OpenRouter;

namespace SNChat.Tests;

public class OpenRouterProviderTests
{
    private static OpenRouterProvider Provider(IToolRegistry? registry, params string[] responses) =>
        new(new HttpClient(new StubHandler(responses)),
            NullLogger<OpenRouterProvider>.Instance,
            apiKey: "test-key",
            toolRegistry: registry);

    private static OpenRouterProvider Provider(string response) => Provider(null, response);

    private static OpenRouterProvider ProviderWithSelection(string response, params string[] selected) =>
        new(new HttpClient(new StubHandler(response)),
            NullLogger<OpenRouterProvider>.Instance,
            apiKey: "test-key",
            selectedModels: selected);

    /// <summary>
    /// Field names copied from a live GET /api/v1/models response. A rename on
    /// either side shows up here as an empty list rather than at runtime as an
    /// empty dropdown.
    /// </summary>
    private const string ModelsJson = """
    {"data":[
      {"id":"z-ai/glm-5.2:free","name":"Z.AI: GLM 5.2 (free)","context_length":256000,
       "pricing":{"prompt":"0","completion":"0"},
       "supported_parameters":["tools","temperature","max_tokens"]},
      {"id":"minimax/minimax-m3:free","name":"MiniMax M3 (free)","context_length":1048576,
       "pricing":{"prompt":"0.0000000","completion":"0"},
       "supported_parameters":["tools"]},
      {"id":"some/paid-model","name":"Paid","context_length":200000,
       "pricing":{"prompt":"0.000003","completion":"0.000015"},
       "supported_parameters":["tools"]},
      {"id":"some/free-no-tools:free","name":"Free but toolless","context_length":8192,
       "pricing":{"prompt":"0","completion":"0"},
       "supported_parameters":["temperature"]}
    ]}
    """;

    /// <summary>
    /// Paid models belong in the list: with a provider key registered on
    /// OpenRouter, the paid id is the one that bills to your own quota.
    /// </summary>
    [Fact]
    public async Task GetAvailableModels_keeps_every_tool_capable_model()
    {
        var models = await Provider(ModelsJson).GetAvailableModelsAsync();

        Assert.Equal(
            new[] { "minimax/minimax-m3:free", "some/paid-model", "z-ai/glm-5.2:free" },
            models.Select(m => m.Id).ToArray());
    }

    [Fact]
    public async Task GetAvailableModels_drops_models_that_cannot_call_tools()
    {
        var models = await Provider(ModelsJson).GetAvailableModelsAsync();

        Assert.DoesNotContain(models, m => m.Id == "some/free-no-tools:free");
    }

    /// <summary>
    /// The point of the picker: the main dropdown shows only what was ticked,
    /// not the several hundred models OpenRouter carries.
    /// </summary>
    [Fact]
    public async Task GetAvailableModels_narrows_to_the_user_selection()
    {
        var provider = ProviderWithSelection(ModelsJson, "z-ai/glm-5.2:free");

        var models = await provider.GetAvailableModelsAsync();

        Assert.Equal(new[] { "z-ai/glm-5.2:free" }, models.Select(m => m.Id).ToArray());
    }

    /// <summary>The picker itself must still see everything, or nothing could be added back.</summary>
    [Fact]
    public async Task GetAllToolCapableModels_ignores_the_selection()
    {
        var provider = ProviderWithSelection(ModelsJson, "z-ai/glm-5.2:free");

        var models = await provider.GetAllToolCapableModelsAsync();

        Assert.Equal(3, models.Count);
    }

    /// <summary>
    /// A saved selection can go stale when models are renamed or withdrawn.
    /// Falling back to the full list keeps the dropdown usable; showing nothing
    /// would strand the user with no way to fix it from the main window.
    /// </summary>
    [Fact]
    public async Task Selection_that_matches_nothing_falls_back_to_all_models()
    {
        var provider = ProviderWithSelection(ModelsJson, "withdrawn/model-that-no-longer-exists");

        var models = await provider.GetAvailableModelsAsync();

        Assert.Equal(3, models.Count);
    }

    [Fact]
    public async Task GetAvailableModels_reads_the_real_context_window()
    {
        var models = await Provider(ModelsJson).GetAvailableModelsAsync();

        Assert.Equal(1048576, models.Single(m => m.Id == "minimax/minimax-m3:free").ContextWindow);
    }

    /// <summary>
    /// A paid model priced below 1.0 must not be flagged free. Guards the
    /// invariant-culture parse: under a comma-decimal locale "0.000003" would
    /// otherwise be read as the number 3.
    /// </summary>
    [Fact]
    public async Task GetAvailableModels_flags_free_and_paid_apart()
    {
        var models = await Provider(ModelsJson).GetAvailableModelsAsync();

        Assert.Equal(false, models.Single(m => m.Id == "some/paid-model").Capabilities["free"]);
        Assert.Equal(true, models.Single(m => m.Id == "z-ai/glm-5.2:free").Capabilities["free"]);
    }

    /// <summary>
    /// OpenAI-style streaming splits one tool call across many chunks: the name
    /// arrives once, then the JSON arguments a few characters at a time, tied
    /// together only by "index". Reassembling that is the part most likely to
    /// be wrong, so it is pinned here end to end.
    /// </summary>
    [Fact]
    public async Task Streaming_reassembles_a_tool_call_split_across_chunks()
    {
        var registry = new RecordingRegistry();
        var toolCallTurn = string.Join("\n", new[]
        {
            ": OPENROUTER PROCESSING",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"web_search","arguments":"{\"query\":"}}]}}]}""",
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"ollama\"}"}}]}}]}""",
            "data: [DONE]",
            "",
        });
        var answerTurn = string.Join("\n", new[]
        {
            """data: {"choices":[{"delta":{"content":"Ollama is a local LLM runner."}}]}""",
            "data: [DONE]",
            "",
        });

        var text = new StringBuilder();
        await foreach (var chunk in Provider(registry, toolCallTurn, answerTurn).GenerateStreamAsync(NewRequest()))
        {
            if (!chunk.IsStatus)
                text.Append(chunk.Content);
        }

        var call = Assert.Single(registry.Calls);
        Assert.Equal("web_search", call.Name);
        Assert.Equal("ollama", call.Arguments["query"]);
        Assert.Equal("call_1", call.Id);

        // The turn ends with the model's answer, not with the tool result.
        Assert.Equal("Ollama is a local LLM runner.", text.ToString());
    }

    [Fact]
    public async Task Streaming_yields_content_and_skips_keepalive_comments()
    {
        var sse = string.Join("\n", new[]
        {
            ": OPENROUTER PROCESSING",
            """data: {"choices":[{"delta":{"content":"Hello"}}]}""",
            """data: {"choices":[{"delta":{"content":" world"}}]}""",
            "data: [DONE]",
            "",
        });

        var text = new StringBuilder();
        await foreach (var chunk in Provider(sse).GenerateStreamAsync(NewRequest()))
            text.Append(chunk.Content);

        Assert.Equal("Hello world", text.ToString());
    }

    /// <summary>
    /// Reasoning is progress, not answer text. It must surface as a status
    /// chunk so it is displayed but never saved into the conversation.
    /// </summary>
    [Fact]
    public async Task Streaming_reports_reasoning_as_status_not_content()
    {
        var sse = string.Join("\n", new[]
        {
            """data: {"choices":[{"delta":{"reasoning":"thinking hard"}}]}""",
            """data: {"choices":[{"delta":{"content":"answer"}}]}""",
            "data: [DONE]",
            "",
        });

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in Provider(sse).GenerateStreamAsync(NewRequest()))
            chunks.Add(chunk);

        Assert.Contains(chunks, c => c.IsStatus && c.Content.Contains("thinking hard"));
        Assert.Equal("answer", string.Concat(chunks.Where(c => !c.IsStatus).Select(c => c.Content)));
    }

    /// <summary>
    /// A model that keeps asking for tools must be cut off rather than looping
    /// forever. Each round is a paid request, so an unbounded loop costs money.
    /// </summary>
    [Fact]
    public async Task Streaming_stops_once_the_tool_iteration_budget_runs_out()
    {
        var registry = new RecordingRegistry();
        var alwaysCallsTool = string.Join("\n", new[]
        {
            """data: {"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c","function":{"name":"web_search","arguments":"{}"}}]}}]}""",
            "data: [DONE]",
            "",
        });

        var request = NewRequest();
        request.MaxToolIterations = 3;

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in Provider(registry, alwaysCallsTool).GenerateStreamAsync(request))
            chunks.Add(chunk);

        Assert.Equal(3, registry.Calls.Count);
        Assert.Contains(chunks, c => c.Content.Contains("too many tool calls"));
    }

    /// <summary>
    /// Body copied from a real 429. OpenRouter sets "message" to the constant
    /// "Provider returned error" and puts the reason in metadata.raw, so
    /// surfacing only the message tells the user nothing at all.
    /// </summary>
    [Fact]
    public async Task Upstream_error_surfaces_the_detail_not_the_generic_message()
    {
        const string body = """
        {"error":{"message":"Provider returned error","code":429,
          "metadata":{"raw":"google/gemma-4-26b-a4b-it:free is temporarily rate-limited upstream. Please retry shortly, or add your own key to accumulate your rate limits",
                      "provider_name":"Google AI Studio"}}}
        """;

        var provider = new OpenRouterProvider(
            new HttpClient(new FailingHandler(HttpStatusCode.TooManyRequests, body)),
            NullLogger<OpenRouterProvider>.Instance,
            apiKey: "test-key");

        var text = new StringBuilder();
        await foreach (var chunk in provider.GenerateStreamAsync(NewRequest()))
            text.Append(chunk.Content);

        Assert.Contains("rate-limited upstream", text.ToString());
        Assert.Contains("retry shortly", text.ToString());
    }

    // --- Bring-your-own-key provider routing ---

    private static readonly Dictionary<string, string> Byok =
        new() { ["google/"] = "google-ai-studio" };

    private const string PlainAnswer = """
        data: {"choices":[{"delta":{"content":"ok"}}]}

        data: [DONE]

        """;

    private static async Task<JsonElement> SendAndCaptureBody(string model)
    {
        var handler = new StubHandler(PlainAnswer);
        var provider = new OpenRouterProvider(
            new HttpClient(handler), NullLogger<OpenRouterProvider>.Instance,
            apiKey: "test-key", byokProviders: Byok);

        var request = NewRequest();
        request.Model = model;

        await foreach (var _ in provider.GenerateStreamAsync(request)) { }

        return JsonDocument.Parse(handler.SentBodies.Single()).RootElement.Clone();
    }

    /// <summary>
    /// Without pinning, a request your own key cannot serve is retried against
    /// another provider and billed to OpenRouter credits instead of your quota.
    /// </summary>
    [Fact]
    public async Task Byok_model_is_pinned_to_its_provider_with_fallbacks_off()
    {
        var body = await SendAndCaptureBody("google/gemma-4-31b-it");

        var routing = body.GetProperty("provider");
        Assert.Equal("google-ai-studio", routing.GetProperty("only")[0].GetString());
        Assert.False(routing.GetProperty("allow_fallbacks").GetBoolean());
    }

    /// <summary>
    /// ":free" ids are the shared-pool variants, which a personal key has no
    /// bearing on. Pinning them would strip the fallbacks they rely on.
    /// </summary>
    [Fact]
    public async Task Free_variant_of_a_byok_model_is_left_unpinned()
    {
        var body = await SendAndCaptureBody("google/gemma-4-31b-it:free");

        Assert.False(body.TryGetProperty("provider", out _));
    }

    [Fact]
    public async Task Model_outside_the_byok_map_is_left_unpinned()
    {
        var body = await SendAndCaptureBody("anthropic/claude-sonnet-4.5");

        Assert.False(body.TryGetProperty("provider", out _));
    }

    /// <summary>
    /// OpenRouter reports usage in the final SSE message, and that message
    /// carries an empty choices array. Reading it has to happen before the
    /// per-choice handling, or the counts are skipped and every reply through
    /// this provider shows no tokens at all.
    /// </summary>
    [Fact]
    public async Task Usage_is_read_from_the_final_chunk_with_no_choices()
    {
        var sse = string.Join("\n", new[]
        {
            """data: {"choices":[{"delta":{"content":"hi"}}]}""",
            """data: {"choices":[],"usage":{"prompt_tokens":6506,"completion_tokens":431,"total_tokens":6937}}""",
            "data: [DONE]",
            "",
        });

        StreamChunk? final = null;
        await foreach (var chunk in Provider(sse).GenerateStreamAsync(NewRequest()))
        {
            if (chunk.IsFinal)
                final = chunk;
        }

        Assert.NotNull(final?.Metadata);
        Assert.Equal(6506, final!.Metadata!.PromptEvalCount);
        Assert.Equal(431, final.Metadata.EvalCount);
    }

    [Fact]
    public async Task Missing_api_key_is_reported_rather_than_sent()
    {
        var provider = new OpenRouterProvider(
            new HttpClient(new StubHandler("should not be reached")),
            NullLogger<OpenRouterProvider>.Instance,
            apiKey: "");

        var chunks = new List<StreamChunk>();
        await foreach (var chunk in provider.GenerateStreamAsync(NewRequest()))
            chunks.Add(chunk);

        Assert.Contains("API key", Assert.Single(chunks).Content);
    }

    private static GenerateRequest NewRequest() => new()
    {
        Model = "z-ai/glm-5.2:free",
        Messages = new List<Message> { new() { Role = MessageRole.User, Content = "hi" } },
        Tools = new[] { new FakeTool() }
    };

    /// <summary>
    /// Replays one body per request. The tool loop sends more than one request
    /// per turn, and a handler that repeated a single tool-call response would
    /// make the model look like it never stops asking for tools.
    /// </summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _bodies;
        public StubHandler(params string[] bodies) => _bodies = new Queue<string>(bodies);

        /// <summary>Request bodies seen, so tests can assert on what was sent.</summary>
        public List<string> SentBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content != null)
                SentBodies.Add(await request.Content.ReadAsStringAsync(cancellationToken));

            // The last body repeats, so single-response tests need no changes.
            var body = _bodies.Count > 1 ? _bodies.Dequeue() : _bodies.Peek();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }

    /// <summary>Returns a non-success status with an error body.</summary>
    private sealed class FailingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public FailingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
    }

    private sealed class FakeTool : ITool
    {
        public string Name => "web_search";
        public string Description => "Search the web";
        public ToolParameterSchema Parameters => new();
        public Task<string> ExecuteAsync(
            IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken = default) =>
            Task.FromResult("result");
    }

    private sealed class RecordingRegistry : IToolRegistry
    {
        public List<ToolCall> Calls { get; } = new();

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken = default)
        {
            Calls.Add(call);
            return Task.FromResult(new ToolResult { ToolCallId = call.Id, Name = call.Name, Content = "result" });
        }

        public IReadOnlyList<ITool> GetTools() => new List<ITool>();
        public ITool? GetTool(string name) => null;
        public void Register(ITool tool) { }
    }
}
