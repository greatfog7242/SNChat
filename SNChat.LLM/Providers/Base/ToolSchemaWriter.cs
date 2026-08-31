using SNChat.Core.Tools;

namespace SNChat.LLM.Providers.Base;

/// <summary>
/// Renders a tool's parameters as JSON Schema for the OpenAI-shaped "function"
/// field that Ollama and OpenRouter both accept.
///
/// Shared so the two providers cannot drift: a schema that one accepts and the
/// other rejects is the hardest kind of tool bug to see, because it only shows
/// up on whichever backend is stricter.
/// </summary>
public static class ToolSchemaWriter
{
    public static Dictionary<string, object?> Write(ToolParameterSchema schema)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = schema.Type,
            ["properties"] = schema.Properties.ToDictionary(
                p => p.Key,
                p => (object?)WriteProperty(p.Value))
        };

        if (schema.Required.Count > 0)
            result["required"] = schema.Required;

        return result;
    }

    /// <summary>
    /// Fields are added only when present, since an explicit null is not the
    /// same as an absent key to a JSON Schema validator.
    /// </summary>
    private static Dictionary<string, object?> WriteProperty(ToolParameterProperty property)
    {
        var result = new Dictionary<string, object?>
        {
            ["type"] = property.Type
        };

        if (!string.IsNullOrEmpty(property.Description))
            result["description"] = property.Description;

        if (property.Enum is { Count: > 0 })
            result["enum"] = property.Enum;

        // An array must declare its element type. Gemini rejects the entire
        // request with INVALID_ARGUMENT when "items" is missing, so a server
        // that declared an array without one gets a permissive default rather
        // than taking every other tool in the same request down with it.
        if (IsArray(property.Type))
        {
            result["items"] = property.Items != null
                ? WriteProperty(property.Items)
                : new Dictionary<string, object?> { ["type"] = "string" };
        }

        if (property.Properties is { Count: > 0 })
        {
            result["properties"] = property.Properties.ToDictionary(
                p => p.Key,
                p => (object?)WriteProperty(p.Value));

            if (property.Required is { Count: > 0 })
                result["required"] = property.Required;
        }

        return result;
    }

    private static bool IsArray(string? type) =>
        string.Equals(type, "array", StringComparison.OrdinalIgnoreCase);
}
