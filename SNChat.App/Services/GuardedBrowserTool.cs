using Microsoft.Extensions.Logging;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.App.Services;

/// <summary>
/// Wraps a chrome-devtools-mcp tool so that navigation stays inside the domain
/// allowlist and file writes fail with a message that names where they may go.
///
/// A decorator like <see cref="GuardedWindowsSystemTool"/>, but simpler because
/// chrome-devtools-mcp publishes a separate tool per operation rather than
/// dispatching on an "action" argument. Guarding keys on the tool name alone.
///
/// Unlike the Windows guard, this one asks the user nothing. The two boundaries
/// it enforces are both settings the user has already decided:
///
/// - **Domains** are checked here because the server has no allowlist of its own.
/// - **File paths** are checked here only to produce a better error. The server
///   enforces its own workspace roots, and SNChat cannot widen them without
///   relaunching it - so a consent dialog would be a question whose "yes" could
///   not be honoured. Checking first means the model is told which folders it
///   may write to instead of being refused by the server without being told.
/// </summary>
public sealed class GuardedBrowserTool : ITool
{
    private readonly ITool _inner;
    private readonly SettingsService _settings;
    private readonly ILogger _logger;

    public string Name => _inner.Name;
    public string Description => _inner.Description;
    public ToolParameterSchema Parameters => _inner.Parameters;

    public GuardedBrowserTool(
        ITool inner,
        SettingsService settings,
        ILogger logger)
    {
        _inner = inner;
        _settings = settings;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var browser = _settings.GetCachedSettings().Browser;

        var blocked = CheckDomain(arguments, browser.AllowedDomains)
                      ?? CheckFilePaths(arguments, browser.WorkspaceRoots);

        if (blocked != null)
            return blocked;

        return await _inner.ExecuteAsync(arguments, cancellationToken);
    }

    /// <summary>
    /// Keeps navigation inside the allowlist. Returns the refusal to send back,
    /// or null to let the call through.
    /// </summary>
    private string? CheckDomain(
        IReadOnlyDictionary<string, object?> arguments,
        IReadOnlyList<string> allowedDomains)
    {
        var url = BrowserGuard.GetUrlArgument(Name, arguments);

        // No URL to check. navigate_page does back, forward and reload this way,
        // and every tool that is not navigation lands here too.
        if (url == null)
            return null;

        if (BrowserGuard.IsUrlAllowed(url, allowedDomains, out var domain))
        {
            if (allowedDomains.Count == 0)
                _logger.LogInformation("{Tool} is visiting {Domain} (no allowlist configured)", Name, domain);
            else
                _logger.LogDebug("{Tool} is visiting {Domain}, which is allowed", Name, domain);

            return null;
        }

        // An unparseable URL reaches here too, with an empty domain.
        if (domain.Length == 0)
            return $"'{url}' is not a URL that can be opened.";

        _logger.LogWarning("{Tool} was blocked from visiting {Domain}", Name, domain);

        return $"{domain} is not on the list of sites this assistant may visit. " +
               $"It may visit: {string.Join(", ", allowedDomains)}. Report that rather than " +
               "trying another address for the same place.";
    }

    /// <summary>
    /// Checks a file path against the server's workspace roots before the server
    /// does, so the refusal can name them. Returns the refusal, or null to let
    /// the call through.
    /// </summary>
    private string? CheckFilePaths(
        IReadOnlyDictionary<string, object?> arguments,
        IReadOnlyList<string> workspaceRoots)
    {
        foreach (var path in BrowserGuard.GetFilePathArguments(Name, arguments))
        {
            if (BrowserGuard.IsWritablePath(path, workspaceRoots, out var refusal))
                continue;

            _logger.LogInformation("{Tool} was refused the path {Path}", Name, path);

            return refusal;
        }

        return null;
    }
}
