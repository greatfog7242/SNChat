using Microsoft.Extensions.Logging;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.MCP;
using SNChat.MCP.Protocol.Messages;

namespace SNChat.App.Services;

/// <summary>
/// Adapts an MCP tool to SNChat's ITool interface.
/// Allows MCP tools from any server to be called by the LLM.
///
/// Also the place where being refused a folder turns into a question for the
/// user. A filesystem server can only reach the folders it was launched with, so
/// asking it for anything else fails - and the assistant, having no way to widen
/// that, would simply report that it cannot read the file. Catching the refusal
/// here means the user can say yes, once, and have the work continue.
/// </summary>
public class McpToolAdapter : ITool
{
    private readonly McpServerConnection _connection;
    private readonly McpTool _mcpTool;
    private readonly ToolParameterSchema _parameters;
    private readonly SessionAccessGrants _grants;
    private readonly IAccessPrompt _prompt;
    private readonly ILogger _logger;

    public string Name => _mcpTool.Name;
    public string Description => _mcpTool.Description ?? $"MCP tool: {_mcpTool.Name}";
    public ToolParameterSchema Parameters => _parameters;

    public McpToolAdapter(
        McpServerConnection connection,
        McpTool mcpTool,
        SessionAccessGrants grants,
        IAccessPrompt prompt,
        ILogger logger)
    {
        _connection = connection;
        _mcpTool = mcpTool;
        _parameters = ConvertSchema(mcpTool.InputSchema);
        _grants = grants;
        _prompt = prompt;
        _logger = logger;
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

            var result = await _connection.CallToolAsync(_mcpTool.Name, mcpArgs, cancellationToken);

            var text = Flatten(result);

            // Only a refusal about a folder is worth asking the user about, and
            // only once - a second failure after a grant is a real failure.
            if (result.IsError == true && McpAccessDenial.TryParse(text, out var refused, out var allowed))
            {
                var widened = await TryWidenAsync(refused, allowed, mcpArgs, cancellationToken);

                if (widened != null)
                    return widened;
            }

            if (result.IsError == true)
                return $"Tool error: {text}";

            return string.IsNullOrEmpty(text)
                ? "Tool executed successfully but returned no content."
                : text;
        }
        catch (OperationCanceledException)
        {
            throw;
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
    /// Asks the user about the folder behind a refused path and, if they agree,
    /// widens the server and runs the call again. Returns the result of that
    /// second attempt, or null to let the original refusal stand.
    /// </summary>
    private async Task<string?> TryWidenAsync(
        string refusedPath,
        string allowedRoots,
        Dictionary<string, object> arguments,
        CancellationToken cancellationToken)
    {
        if (!_connection.CanWiden)
            return null;

        var folder = SessionAccessGrants.FolderToGrant(refusedPath);

        if (folder == null)
            return null;

        // Said no already. Asking again on every retry is how a careful user
        // gets trained into clicking Yes without reading.
        if (_grants.IsRefused(folder))
        {
            return $"Access to {folder} was declined for this session, so {refusedPath} " +
                   "cannot be read. Work with what is already available, and do not ask again.";
        }

        if (!SessionAccessGrants.MayBeGranted(folder, out var reason))
        {
            _logger.LogWarning("Refusing to offer access to {Folder}: {Reason}", folder, reason);

            return $"{refusedPath} cannot be opened, and access to {folder} cannot be granted " +
                   $"from here because {reason}. Only what is under {allowedRoots} is available.";
        }

        // Already granted, yet still refused: the server was restarted without
        // it, or something else is wrong. Retrying would loop.
        if (_grants.IsGranted(folder))
        {
            _logger.LogWarning(
                "{Folder} is granted but {Tool} was still refused", folder, _mcpTool.Name);

            return null;
        }

        _logger.LogInformation(
            "{Tool} was refused {Path}; asking the user about {Folder}",
            _mcpTool.Name, refusedPath, folder);

        bool approved;

        try
        {
            approved = await _prompt.RequestAccessAsync(folder, refusedPath, _mcpTool.Name, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ask about access to {Folder}", folder);
            return null;
        }

        if (!approved)
        {
            _grants.Refuse(folder);
            _logger.LogInformation("The user declined access to {Folder}", folder);

            return $"The user declined access to {folder}, so {refusedPath} cannot be read. " +
                   "Carry on with what is available and do not ask for it again.";
        }

        if (!await _connection.AllowFolderAsync(folder, cancellationToken))
        {
            return $"Access to {folder} was granted, but the file server could not be " +
                   "restarted to take it up. The folder is still unreadable.";
        }

        _grants.Grant(folder);
        _logger.LogInformation("Granted access to {Folder} for this session", folder);

        // The same call again, against the restarted server. The arguments are
        // passed down rather than held on the adapter: tools must be safe to
        // call concurrently, and a field would hand one call's path to another's
        // retry.
        var retry = await _connection.CallToolAsync(_mcpTool.Name, arguments, cancellationToken);
        var retryText = Flatten(retry);

        if (retry.IsError == true)
            return $"Tool error: {retryText}";

        return string.IsNullOrEmpty(retryText)
            ? "Tool executed successfully but returned no content."
            : retryText;
    }

    private static string Flatten(CallToolResult result) =>
        string.Join("\n\n", result.Content
            .Where(c => !string.IsNullOrEmpty(c.Text))
            .Select(c => c.Text));

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
