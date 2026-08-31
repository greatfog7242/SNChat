using System.Text.Json;
using SNChat.MCP.Protocol.Messages;

namespace SNChat.Tests;

/// <summary>
/// The array item schemas were originally lost here, at deserialization, before
/// any conversion ran - the DTO simply had no field for "items", so the data
/// was gone the moment a server described its tools.
/// </summary>
public class McpSchemaDeserializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>Shape emitted by @modelcontextprotocol/server-filesystem.</summary>
    private const string ReadMultipleFilesSchema = """
    {"type":"object",
     "properties":{"paths":{"type":"array","items":{"type":"string"}}},
     "required":["paths"]}
    """;

    private const string EditFileSchema = """
    {"type":"object",
     "properties":{
       "path":{"type":"string"},
       "edits":{"type":"array","items":{"type":"object",
         "properties":{"oldText":{"type":"string","description":"Text to search for"},
                       "newText":{"type":"string","description":"Text to replace with"}},
         "required":["oldText","newText"]}}},
     "required":["path","edits"]}
    """;

    [Fact]
    public void Array_of_strings_keeps_its_item_type()
    {
        var schema = JsonSerializer.Deserialize<ToolInputSchema>(ReadMultipleFilesSchema, Options);

        var paths = schema!.Properties!["paths"];
        Assert.Equal("array", paths.Type);
        Assert.Equal("string", paths.Items!.Type);
    }

    [Fact]
    public void Array_of_objects_keeps_the_nested_fields()
    {
        var schema = JsonSerializer.Deserialize<ToolInputSchema>(EditFileSchema, Options);

        var edits = schema!.Properties!["edits"];
        Assert.Equal("array", edits.Type);

        var item = edits.Items!;
        Assert.Equal("object", item.Type);
        Assert.Equal("Text to search for", item.Properties!["oldText"].Description);
        Assert.Equal(new[] { "oldText", "newText" }, item.Required!.ToArray());
    }
}
