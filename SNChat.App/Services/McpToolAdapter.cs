using SNChat.Core.Tools;
using SNChat.MCP;
using SNChat.MCP.Protocol.Messages;

namespace SNChat.App.Services;

/// <summary>
/// Adapts an MCP tool to SNChat's ITool interface.
/// Allows MCP tools from any server to be called by the LLM.
/// </summary>
public class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly McpTool _mcpTool;
    private readonly ToolParameterSchema _parameters;

    public string Name => _mcpTool.Name;
    public string Description => _mcpTool.Description ?? $"MCP tool: {_mcpTool.Name}";
    public ToolParameterSchema Parameters => _parameters;

    public McpToolAdapter(McpClient client, McpTool mcpTool)
    {
        _client = client;
        _mcpTool = mcpTool;
        _parameters = ConvertSchema(mcpTool.InputSchema);
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Convert arguments to MCP format (non-nullable dictionary)
            var mcpArgs = arguments
                .Where(kvp => kvp.Value != null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

            // Call the MCP tool
            var result = await _client.CallToolAsync(_mcpTool.Name, mcpArgs, cancellationToken);

            // Check if tool returned an error
            if (result.IsError == true)
            {
                var errorText = string.Join("\n", result.Content.Select(c => c.Text ?? ""));
                return $"Tool error: {errorText}";
            }

            // Combine all content items into a single response
            var response = string.Join("\n\n", result.Content
                .Where(c => !string.IsNullOrEmpty(c.Text))
                .Select(c => c.Text));

            return string.IsNullOrEmpty(response)
                ? "Tool executed successfully but returned no content."
                : response;
        }
        catch (McpException ex)
        {
            // Return MCP errors as text so the model can see what went wrong
            return $"MCP error calling {_mcpTool.Name}: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error executing MCP tool {_mcpTool.Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Converts MCP's ToolInputSchema to SNChat's ToolParameterSchema.
    /// The formats are very similar, both based on JSON Schema.
    ///
    /// Nested structure is copied rather than flattened: an array that loses its
    /// "items", or an object that loses its fields, produces a schema that some
    /// providers reject and that leaves the model guessing at argument shapes.
    /// </summary>
    private static ToolParameterSchema ConvertSchema(ToolInputSchema mcpSchema)
    {
        var schema = new ToolParameterSchema
        {
            Type = mcpSchema.Type ?? "object",
            Required = mcpSchema.Required ?? new List<string>()
        };

        if (mcpSchema.Properties != null)
        {
            foreach (var (name, prop) in mcpSchema.Properties)
                schema.Properties[name] = ConvertProperty(prop);
        }

        return schema;
    }

    private static ToolParameterProperty ConvertProperty(PropertySchema prop) => new()
    {
        Type = prop.Type ?? "string",
        Description = prop.Description ?? "",
        Enum = prop.Enum,
        Items = prop.Items == null ? null : ConvertProperty(prop.Items),
        Properties = prop.Properties?.ToDictionary(p => p.Key, p => ConvertProperty(p.Value)),
        Required = prop.Required
    };
}
