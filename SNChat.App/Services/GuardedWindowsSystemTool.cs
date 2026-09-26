using Microsoft.Extensions.Logging;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.App.Services;

/// <summary>
/// Wraps a windows-system-mcp tool so that the calls which change the machine,
/// or read the contents of a file, have to be agreed to first.
///
/// A decorator rather than a change to <see cref="McpToolAdapter"/> because the
/// adapter is generic over every MCP server and this is about one server's
/// particular habits. The model sees no difference: same name, same description,
/// same schema. The only thing that changes is what happens between the call and
/// the server.
///
/// It stands in front of the server rather than configuring it, because there is
/// nothing to configure - windows-system-mcp takes no allowed-directories list
/// and no read-only flag. Either the check happens here or it does not happen.
/// </summary>
public sealed class GuardedWindowsSystemTool : ITool
{
    private readonly ITool _inner;
    private readonly SettingsService _settings;
    private readonly SessionAccessGrants _grants;
    private readonly IAccessPrompt _accessPrompt;
    private readonly IActionPrompt _actionPrompt;
    private readonly WindowsSystemActions _actions;
    private readonly ILogger _logger;

    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public ToolParameterSchema Parameters => _inner.Parameters;

    public GuardedWindowsSystemTool(
        ITool inner,
        SettingsService settings,
        SessionAccessGrants grants,
        IAccessPrompt accessPrompt,
        IActionPrompt actionPrompt,
        WindowsSystemActions actions,
        ILogger logger)
    {
        _inner = inner;
        _settings = settings;
        _grants = grants;
        _accessPrompt = accessPrompt;
        _actionPrompt = actionPrompt;
        _actions = actions;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var blocked = await CheckFileAccessAsync(arguments, cancellationToken)
                      ?? await CheckDestructiveAsync(arguments, cancellationToken);

        if (blocked != null)
            return blocked;

        var result = await _inner.ExecuteAsync(arguments, cancellationToken);

        // The server rejects an action it does not have without saying what it
        // does have, or which of its sibling tools owns the one that was asked
        // for. Left alone, the model guesses again until it runs out of turns.
        var explanation = _actions.Explain(Name, result);

        return explanation == null ? result : $"{result}\n\n{explanation}";
    }

    /// <summary>
    /// Reading a file's contents needs the folder to be consented to, through
    /// the same grants the filesystem MCP server uses. Returns the refusal to
    /// send back, or null to let the call through.
    /// </summary>
    private async Task<string?> CheckFileAccessAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var path = WindowsSystemGuard.FileToConsentTo(Name, arguments);

        if (path == null || _grants.IsGranted(path))
            return null;

        var folder = SessionAccessGrants.FolderToGrant(path);

        if (folder == null)
            return $"'{path}' is not a path that can be read.";

        if (_grants.IsRefused(folder))
        {
            return $"Access to {folder} was declined for this session, so {path} cannot be " +
                   "read. Work with what is already available, and do not ask again.";
        }

        if (!SessionAccessGrants.MayBeGranted(folder, out var reason))
        {
            _logger.LogWarning("Refusing to offer access to {Folder}: {Reason}", folder, reason);

            return $"{path} cannot be read, because access to {folder} cannot be granted " +
                   $"from here: {reason}. Report that rather than trying another path into " +
                   "the same place.";
        }

        bool approved;

        try
        {
            approved = await _accessPrompt.RequestAccessAsync(folder, path, Name, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ask about access to {Folder}", folder);
            return $"{path} cannot be read just now.";
        }

        if (!approved)
        {
            _grants.Refuse(folder);
            _logger.LogInformation("The user declined access to {Folder}", folder);

            return $"The user declined access to {folder}, so {path} cannot be read. " +
                   "Carry on with what is available and do not ask for it again.";
        }

        _grants.Grant(folder);
        _logger.LogInformation("Granted access to {Folder} for this session", folder);

        return null;
    }

    /// <summary>
    /// Killing a process, moving a service, or scanning someone else's machine
    /// needs the switch to be on and the user to say yes to this particular one.
    /// Returns the refusal to send back, or null to let the call through.
    /// </summary>
    private async Task<string?> CheckDestructiveAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        if (!WindowsSystemGuard.NeedsConsent(Name, arguments, out var confirmable))
            return null;

        var settings = _settings.GetCachedSettings().WindowsSystem;

        if (!WindowsSystemGuard.IsPermitted(confirmable.Capability, settings, out var refusal))
        {
            _logger.LogInformation(
                "{Tool} refused: {Capability} is turned off", Name, confirmable.Capability);

            return refusal;
        }

        _logger.LogInformation(
            "{Tool} wants to {Summary} - {Detail}; asking the user",
            Name, confirmable.Summary, confirmable.Detail);

        bool approved;

        try
        {
            approved = await _actionPrompt.ConfirmAsync(
                confirmable.Summary, confirmable.Detail, Name, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not ask about {Summary}", confirmable.Summary);
            return "That could not be confirmed with the user, so it was not done.";
        }

        if (!approved)
        {
            _logger.LogInformation("The user declined: {Detail}", confirmable.Detail);

            // Not remembered, unlike a folder refusal. A folder is a standing
            // question the model will hit again and again while it works; this
            // is one act on one target, and a no to stopping a service now is
            // not a no to stopping a different one later.
            return $"The user declined. {confirmable.Detail} was left alone. " +
                   "Tell them it was not done, and do not retry it.";
        }

        return null;
    }
}
