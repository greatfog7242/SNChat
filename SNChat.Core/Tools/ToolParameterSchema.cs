namespace SNChat.Core.Tools;

/// <summary>
/// Minimal JSON Schema object description. Kept provider-agnostic here; each
/// provider translates it into whatever shape its API expects.
/// </summary>
public class ToolParameterSchema
{
    public string Type { get; set; } = "object";
    public Dictionary<string, ToolParameterProperty> Properties { get; set; } = new();
    public List<string> Required { get; set; } = new();
}

public class ToolParameterProperty
{
    public string Type { get; set; } = "string";
    public string Description { get; set; } = string.Empty;

    /// <summary>Optional allowed values, emitted as JSON Schema "enum".</summary>
    public List<string>? Enum { get; set; }

    /// <summary>
    /// Element schema for Type == "array". Required by JSON Schema, and Gemini
    /// rejects the whole request with INVALID_ARGUMENT when an array parameter
    /// arrives without it, so an array property must always carry one.
    /// </summary>
    public ToolParameterProperty? Items { get; set; }

    /// <summary>
    /// Field schemas for Type == "object". Without these a nested object is
    /// described only as "object" and the model has to guess its shape.
    /// </summary>
    public Dictionary<string, ToolParameterProperty>? Properties { get; set; }

    /// <summary>Names of required fields, when this property is an object.</summary>
    public List<string>? Required { get; set; }
}
