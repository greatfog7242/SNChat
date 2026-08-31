using System.Text.Json.Serialization;

namespace SNChat.LLM.Providers.OpenRouter;

// OpenRouter speaks the OpenAI chat schema, so these shapes are the OpenAI
// ones plus a few OpenRouter additions (pricing, supported_parameters,
// reasoning). Kept separate from the FreeToken DTOs because those model no
// tool calling at all.

public class OpenRouterChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<OpenRouterMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; set; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; set; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenRouterTool>? Tools { get; set; }

    [JsonPropertyName("provider")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenRouterProviderRouting? Provider { get; set; }
}

/// <summary>
/// Restricts which upstream provider may serve a request. Used to keep
/// bring-your-own-key traffic on the provider the key belongs to.
/// </summary>
public class OpenRouterProviderRouting
{
    [JsonPropertyName("only")]
    public List<string> Only { get; set; } = new();

    [JsonPropertyName("allow_fallbacks")]
    public bool AllowFallbacks { get; set; }
}

public class OpenRouterMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string? Content { get; set; } = string.Empty;

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenRouterToolCall>? ToolCalls { get; set; }

    /// <summary>Ties a role="tool" result back to the call that asked for it.</summary>
    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; set; }
}

public class OpenRouterTool
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenRouterFunction Function { get; set; } = new();
}

public class OpenRouterFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public object Parameters { get; set; } = new();
}

public class OpenRouterToolCall
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenRouterToolCallFunction Function { get; set; } = new();
}

public class OpenRouterToolCallFunction
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// A JSON-encoded string, not an object - the opposite of Ollama. While
    /// streaming it arrives in fragments that have to be concatenated before
    /// the whole thing parses.
    /// </summary>
    [JsonPropertyName("arguments")]
    public string Arguments { get; set; } = string.Empty;
}

// Streaming response

public class OpenRouterChatResponse
{
    [JsonPropertyName("choices")]
    public List<OpenRouterChoice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public OpenRouterUsage? Usage { get; set; }
}

public class OpenRouterChoice
{
    [JsonPropertyName("delta")]
    public OpenRouterDelta? Delta { get; set; }

    [JsonPropertyName("message")]
    public OpenRouterDelta? Message { get; set; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }
}

public class OpenRouterDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }

    /// <summary>
    /// Reasoning models expose their chain of thought here. Shown as a status
    /// line and never persisted, matching how Ollama's "thinking" is handled.
    /// </summary>
    [JsonPropertyName("reasoning")]
    public string? Reasoning { get; set; }

    [JsonPropertyName("tool_calls")]
    public List<OpenRouterToolCallDelta>? ToolCalls { get; set; }
}

/// <summary>
/// A partial tool call. OpenAI-style streaming splits one call across many
/// chunks, tying the pieces together by <see cref="Index"/> rather than by id,
/// so fragments are accumulated per index until the stream ends.
/// </summary>
public class OpenRouterToolCallDelta
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("function")]
    public OpenRouterToolCallFunctionDelta? Function { get; set; }
}

public class OpenRouterToolCallFunctionDelta
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

public class OpenRouterUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}

// Models list

public class OpenRouterModelsResponse
{
    [JsonPropertyName("data")]
    public List<OpenRouterModelInfo>? Data { get; set; }
}

public class OpenRouterModelInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; set; }

    [JsonPropertyName("pricing")]
    public OpenRouterPricing? Pricing { get; set; }

    /// <summary>
    /// Contains "tools" when the model can call tools. Used to filter the list,
    /// since a model without it fails once web search is switched on.
    /// </summary>
    [JsonPropertyName("supported_parameters")]
    public List<string>? SupportedParameters { get; set; }
}

/// <summary>
/// Per-token prices as decimal strings ("0.000003"), not numbers. Free models
/// report "0".
/// </summary>
public class OpenRouterPricing
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("completion")]
    public string? Completion { get; set; }
}
