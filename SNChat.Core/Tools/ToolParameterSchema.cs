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
}
