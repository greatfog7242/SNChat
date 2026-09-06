using System.Text.Json;

namespace SNChat.LLM.Providers.Base;

/// <summary>
/// Turns the JSON arguments a model sent for a tool call into plain CLR values.
///
/// Shared by the providers because getting it wrong is invisible until a tool
/// with a non-scalar parameter is used. Both providers used to render anything
/// that was not a string, number or boolean with GetRawText(), which meant an
/// array arrived as a *string of JSON*:
///
///     "edits": "[{\"oldText\":\"a\",\"newText\":\"b\"}]"
///
/// A server that validates its schema then rejects every such call - the MCP
/// filesystem server answers "expected array, received string at edits" - so
/// edit_file failed one hundred percent of the time while every tool taking
/// only scalars worked perfectly. That pattern reads exactly like a model too
/// weak to use the tool, which is the wrong thing to conclude.
/// </summary>
public static class ToolArgumentReader
{
    /// <summary>
    /// The arguments as a dictionary. A non-object - which a confused model can
    /// produce - yields no arguments rather than throwing, leaving the tool to
    /// report what it needed.
    /// </summary>
    public static Dictionary<string, object?> Read(JsonElement root)
    {
        var result = new Dictionary<string, object?>();

        if (root.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in root.EnumerateObject())
            result[property.Name] = ReadValue(property.Value);

        return result;
    }

    /// <summary>
    /// One value, with arrays and objects kept as real structures so they
    /// serialize back out as JSON arrays and objects rather than as strings.
    /// </summary>
    public static object? ReadValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        // Cast to object explicitly: without it the conditional's common type is
        // double, so every whole number would be widened and a tool expecting an
        // integer would receive 3.0 where the model sent 3.
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Undefined => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ReadValue).ToList(),
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => ReadValue(p.Value)),
        _ => element.GetRawText()
    };
}
