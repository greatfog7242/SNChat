using System.Text.Json;
using SNChat.LLM.Providers.Base;

namespace SNChat.Tests;

/// <summary>
/// Arguments a model sends for a tool call have to survive the trip to the tool
/// with their shape intact. Anything that is not a string, number or boolean
/// used to be rendered with GetRawText(), so an array reached the tool as a
/// string of JSON - which a server that validates its schema rejects outright.
/// </summary>
public class ToolArgumentReaderTests
{
    private static Dictionary<string, object?> Read(string json) =>
        ToolArgumentReader.Read(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Scalars_come_through_as_themselves()
    {
        var arguments = Read("""
            { "path": "C:\\src\\a.txt", "count": 3, "ratio": 1.5, "dryRun": true, "nothing": null }
            """);

        Assert.Equal("C:\\src\\a.txt", arguments["path"]);
        Assert.Equal(3L, arguments["count"]);
        Assert.Equal(1.5, arguments["ratio"]);
        Assert.Equal(true, arguments["dryRun"]);
        Assert.Null(arguments["nothing"]);
    }

    [Fact]
    public void An_array_stays_a_list_rather_than_becoming_a_string()
    {
        var arguments = Read("""{ "paths": ["a.txt", "b.txt"] }""");

        var paths = Assert.IsType<List<object?>>(arguments["paths"]);
        Assert.Equal(new object?[] { "a.txt", "b.txt" }, paths);
    }

    [Fact]
    public void An_object_stays_a_dictionary_rather_than_becoming_a_string()
    {
        var arguments = Read("""{ "options": { "deep": true, "name": "x" } }""");

        var options = Assert.IsType<Dictionary<string, object?>>(arguments["options"]);
        Assert.Equal(true, options["deep"]);
        Assert.Equal("x", options["name"]);
    }

    [Fact]
    public void Nesting_is_preserved_all_the_way_down()
    {
        var arguments = Read("""{ "edits": [ { "tags": ["a", "b"] } ] }""");

        var edits = Assert.IsType<List<object?>>(arguments["edits"]);
        var first = Assert.IsType<Dictionary<string, object?>>(edits[0]);
        var tags = Assert.IsType<List<object?>>(first["tags"]);

        Assert.Equal("b", tags[1]);
    }

    /// <summary>
    /// The reported bug, end to end: the arguments a model sends for the MCP
    /// filesystem server's edit_file, serialized again the way the MCP client
    /// does. "edits" has to leave as a JSON array. As a string, the server
    /// answers "expected array, received string at edits" and the edit fails -
    /// every time, which looks like a model that cannot use the tool.
    /// </summary>
    [Fact]
    public void The_edit_file_arguments_serialize_back_out_as_an_array()
    {
        var arguments = Read("""
            {
              "path": "C:\\ai-playground\\main.cpp",
              "edits": [ { "oldText": "int main()", "newText": "int main(int argc, char** argv)" } ]
            }
            """);

        var json = JsonSerializer.Serialize(arguments);

        using var round = JsonDocument.Parse(json);
        var edits = round.RootElement.GetProperty("edits");

        Assert.Equal(JsonValueKind.Array, edits.ValueKind);

        var edit = edits[0];
        Assert.Equal(JsonValueKind.Object, edit.ValueKind);
        Assert.Equal("int main()", edit.GetProperty("oldText").GetString());
        Assert.Equal("int main(int argc, char** argv)", edit.GetProperty("newText").GetString());
    }

    /// <summary>
    /// The same thing again, but serialized through the actual JSON-RPC
    /// parameters the MCP client puts on the wire rather than a dictionary that
    /// merely resembles them. The previous test would still pass if the client
    /// wrapped the arguments in something that changed their shape.
    /// </summary>
    [Fact]
    public void The_edits_array_survives_into_the_json_rpc_parameters()
    {
        var arguments = Read("""
            {
              "path": "C:\\ai-playground\\main.cpp",
              "edits": [ { "oldText": "a", "newText": "b" } ]
            }
            """);

        var parameters = new SNChat.MCP.Protocol.Messages.CallToolParams
        {
            Name = "edit_file",
            Arguments = arguments
                .Where(pair => pair.Value != null)
                .ToDictionary(pair => pair.Key, pair => pair.Value!)
        };

        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(parameters));
        var edits = wire.RootElement.GetProperty("arguments").GetProperty("edits");

        Assert.Equal(JsonValueKind.Array, edits.ValueKind);
        Assert.Equal("a", edits[0].GetProperty("oldText").GetString());
    }

    [Fact]
    public void An_empty_array_is_still_an_array()
    {
        var json = JsonSerializer.Serialize(Read("""{ "edits": [] }"""));

        Assert.Equal(JsonValueKind.Array,
            JsonDocument.Parse(json).RootElement.GetProperty("edits").ValueKind);
    }

    [Fact]
    public void Arguments_that_are_not_an_object_yield_nothing_rather_than_throwing()
    {
        // A confused model can send anything; the tool then reports what it
        // needed instead of the turn falling over.
        Assert.Empty(Read("\"just a string\""));
        Assert.Empty(Read("[1, 2, 3]"));
    }
}
