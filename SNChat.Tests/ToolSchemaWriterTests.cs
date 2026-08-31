using System.Text.Json;
using SNChat.Core.Tools;
using SNChat.LLM.Providers.Base;

namespace SNChat.Tests;

public class ToolSchemaWriterTests
{
    private static JsonElement WriteAndParse(ToolParameterSchema schema) =>
        JsonSerializer.SerializeToElement(ToolSchemaWriter.Write(schema));

    private static ToolParameterSchema SchemaWith(string name, ToolParameterProperty property) =>
        new() { Properties = new Dictionary<string, ToolParameterProperty> { [name] = property } };

    /// <summary>
    /// The bug this guards: a filesystem MCP server declares "paths" as an array
    /// of strings, the item schema was dropped in conversion, and Gemini
    /// rejected the whole request with
    /// "properties[paths].items: missing field" - taking every other tool in
    /// that request down with it.
    /// </summary>
    [Fact]
    public void Array_property_emits_its_item_schema()
    {
        var json = WriteAndParse(SchemaWith("paths", new ToolParameterProperty
        {
            Type = "array",
            Description = "Paths to read",
            Items = new ToolParameterProperty { Type = "string" }
        }));

        var items = json.GetProperty("properties").GetProperty("paths").GetProperty("items");
        Assert.Equal("string", items.GetProperty("type").GetString());
    }

    /// <summary>
    /// A server may declare an array with no item schema at all. Emitting the
    /// array without "items" would fail the request, so a permissive default
    /// stands in - a usable tool beats a rejected request.
    /// </summary>
    [Fact]
    public void Array_property_without_items_still_emits_a_default()
    {
        var json = WriteAndParse(SchemaWith("excludePatterns", new ToolParameterProperty
        {
            Type = "array"
        }));

        var property = json.GetProperty("properties").GetProperty("excludePatterns");
        Assert.True(property.TryGetProperty("items", out var items));
        Assert.Equal("string", items.GetProperty("type").GetString());
    }

    /// <summary>
    /// "edits" on the filesystem server is an array of objects. The object's
    /// fields have to survive or the model cannot construct an edit.
    /// </summary>
    [Fact]
    public void Array_of_objects_keeps_the_nested_field_schemas()
    {
        var json = WriteAndParse(SchemaWith("edits", new ToolParameterProperty
        {
            Type = "array",
            Items = new ToolParameterProperty
            {
                Type = "object",
                Properties = new Dictionary<string, ToolParameterProperty>
                {
                    ["oldText"] = new() { Type = "string", Description = "Text to replace" },
                    ["newText"] = new() { Type = "string", Description = "Replacement" }
                },
                Required = new List<string> { "oldText", "newText" }
            }
        }));

        var items = json.GetProperty("properties").GetProperty("edits").GetProperty("items");
        Assert.Equal("object", items.GetProperty("type").GetString());

        var nested = items.GetProperty("properties");
        Assert.Equal("string", nested.GetProperty("oldText").GetProperty("type").GetString());
        Assert.Equal("Replacement", nested.GetProperty("newText").GetProperty("description").GetString());
        Assert.Equal(2, items.GetProperty("required").GetArrayLength());
    }

    /// <summary>
    /// An absent key and a key set to null are different to a schema validator,
    /// so optional fields must be omitted rather than written as null.
    /// </summary>
    [Fact]
    public void Absent_optional_fields_are_omitted_not_null()
    {
        var json = WriteAndParse(SchemaWith("query", new ToolParameterProperty { Type = "string" }));

        var property = json.GetProperty("properties").GetProperty("query");
        Assert.False(property.TryGetProperty("enum", out _));
        Assert.False(property.TryGetProperty("items", out _));
        Assert.False(property.TryGetProperty("description", out _));
    }

    [Fact]
    public void Enum_values_are_preserved()
    {
        var json = WriteAndParse(SchemaWith("mode", new ToolParameterProperty
        {
            Type = "string",
            Enum = new List<string> { "fast", "thorough" }
        }));

        var values = json.GetProperty("properties").GetProperty("mode").GetProperty("enum")
            .EnumerateArray().Select(v => v.GetString()).ToArray();

        Assert.Equal(new[] { "fast", "thorough" }, values);
    }

    [Fact]
    public void Required_is_omitted_when_no_field_is_required()
    {
        var json = WriteAndParse(SchemaWith("q", new ToolParameterProperty { Type = "string" }));

        Assert.False(json.TryGetProperty("required", out _));
    }
}
