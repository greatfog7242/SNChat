using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>
/// Recognizes Chrome DevTools MCP tools and determines what guards they need.
///
/// The chrome-devtools-mcp server provides 30 separate tools (unlike
/// windows-system-mcp's action-based dispatch), so guards can key on tool names
/// directly. Tools are categorized into observational (safe), navigation (needs
/// domain check), and excluded high-risk tools (never registered).
///
/// The tool names here were read off the running server (chrome-devtools-mcp v1.10.1)
/// on 2026-09-25. See SQLITE_AND_BROWSER_MCP_PLAN.md.
/// </summary>
public static class BrowserGuard
{
    // Observational tools - safe, read-only access
    public static readonly IReadOnlyList<string> ObservationalTools = new[]
    {
        "list_pages", "select_page", "take_snapshot", "take_screenshot",
        "get_css_styles", "list_console_messages", "get_console_message",
        "list_network_requests", "get_network_request",
        "performance_start_trace", "performance_stop_trace",
        "performance_analyze_insight", "lighthouse_audit",
        "take_heapsnapshot", "wait_for", "resize_page"
    };

    // Navigation tools. Only the two that take a URL are domain-checked;
    // close_page has no URL and closing a tab reaches nothing.
    public static readonly IReadOnlyList<string> NavigationTools = new[]
    {
        "navigate_page", "new_page", "close_page"
    };

    /// <summary>
    /// The navigation tools that actually load a URL, and so have a domain to
    /// check. navigate_page also does back/forward/reload, where no URL is
    /// supplied and nothing new is reached.
    /// </summary>
    public static readonly IReadOnlyList<string> UrlBearingTools = new[]
    {
        "navigate_page", "new_page"
    };

    // Interaction tools - explicitly excluded per user decision
    public static readonly IReadOnlyList<string> InteractionTools = new[]
    {
        "click", "fill", "fill_form", "type_text", "press_key",
        "drag", "hover", "handle_dialog"
    };

    // High-risk tools - explicitly excluded per user decision
    public static readonly IReadOnlyList<string> HighRiskTools = new[]
    {
        "evaluate_script", "emulate", "upload_file"
    };

    /// <summary>
    /// Tools that write a file where they are told to, and the argument names
    /// they take it in. Read off the running server (v1.10.1): most use
    /// "filePath", lighthouse_audit uses "outputDirPath", and get_network_request
    /// writes two separate files.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> FileWritingTools =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["take_screenshot"] = new[] { "filePath" },
            ["take_snapshot"] = new[] { "filePath" },
            ["take_heapsnapshot"] = new[] { "filePath" },
            ["performance_start_trace"] = new[] { "filePath" },
            ["performance_stop_trace"] = new[] { "filePath" },
            ["lighthouse_audit"] = new[] { "outputDirPath" },
            ["get_network_request"] = new[] { "requestFilePath", "responseFilePath" }
        };

    /// <summary>
    /// All Chrome DevTools tools we recognize and allow.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedToolNames =
        ObservationalTools.Concat(NavigationTools).ToList();

    /// <summary>Whether this is a recognized Chrome DevTools tool.</summary>
    public static bool IsBrowserTool(string toolName) =>
        AllowedToolNames.Contains(toolName);

    /// <summary>Whether this tool can load a URL that needs domain checking.</summary>
    public static bool NeedsDomainCheck(string toolName) =>
        UrlBearingTools.Contains(toolName);

    /// <summary>Whether this tool writes files and needs path checking.</summary>
    public static bool WritesFiles(string toolName) =>
        FileWritingTools.ContainsKey(toolName);

    /// <summary>
    /// The URL this call would load, or null when it loads none.
    ///
    /// Null is the ordinary case for navigate_page with type "back", "forward"
    /// or "reload", where "url" is absent and no new origin is reached. Callers
    /// must treat null as "nothing to check" rather than as a refusal.
    /// </summary>
    public static string? GetUrlArgument(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (!NeedsDomainCheck(toolName))
            return null;

        if (arguments.TryGetValue("url", out var value) && value != null)
        {
            var url = value.ToString()?.Trim();

            return string.IsNullOrEmpty(url) ? null : url;
        }

        return null;
    }

    /// <summary>
    /// The file paths this call would write, under the argument names the tool
    /// actually uses. Empty when the tool writes no file, or when it was given
    /// no path and will return its output inline instead.
    /// </summary>
    public static IReadOnlyList<string> GetFilePathArguments(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (!FileWritingTools.TryGetValue(toolName, out var paramNames))
            return Array.Empty<string>();

        var paths = new List<string>();

        foreach (var paramName in paramNames)
        {
            if (arguments.TryGetValue(paramName, out var value) && value != null)
            {
                var path = value.ToString()?.Trim();

                if (!string.IsNullOrEmpty(path))
                    paths.Add(path);
            }
        }

        return paths;
    }

    /// <summary>
    /// Whether a path the model supplied will survive the server's own workspace
    /// check, and what to tell it if not.
    ///
    /// This does not grant anything - chrome-devtools-mcp enforces its workspace
    /// roots itself and SNChat cannot widen them without relaunching the server.
    /// The point is to fail with a message that names the configured folders,
    /// because the server's own refusal names none of them and a model that
    /// cannot see where it may write simply tries another path and fails again.
    /// </summary>
    public static bool IsWritablePath(
        string path,
        IReadOnlyList<string> workspaceRoots,
        out string refusal)
    {
        refusal = string.Empty;

        // The server resolves a bare name against its own working directory,
        // which is wherever SNChat happened to be launched from - never what
        // the model meant.
        if (!Path.IsPathRooted(path))
        {
            refusal = $"'{path}' is a relative path, which the browser resolves against its " +
                      "own working directory rather than anywhere useful. Give a full path " +
                      $"{DescribeRoots(workspaceRoots)}.";
            return false;
        }

        // Nothing configured: the server allows only the OS temp directory.
        if (workspaceRoots.Count == 0)
        {
            var temp = Path.GetTempPath();

            if (IsWithin(path, temp))
                return true;

            refusal = $"The browser may only save files under {temp.TrimEnd(Path.DirectorySeparatorChar)} " +
                      $"at the moment, so '{path}' will be refused. Either save under there, or ask " +
                      "the user to add a folder to Settings - Browser - WorkspaceRoots and restart " +
                      "SNChat. Report that rather than trying other paths.";
            return false;
        }

        if (workspaceRoots.Any(root => IsWithin(path, root)))
            return true;

        refusal = $"'{path}' is outside the folders the browser may write to. It may write " +
                  $"{DescribeRoots(workspaceRoots)}. Save there instead, or report that the folder " +
                  "would have to be added to Settings - Browser - WorkspaceRoots. Do not try " +
                  "other paths outside it.";
        return false;
    }

    private static string DescribeRoots(IReadOnlyList<string> roots) =>
        roots.Count == 0
            ? $"under {Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)}"
            : "under " + string.Join(" or ", roots);

    /// <summary>
    /// Whether a path sits inside a folder. The separator check is what stops
    /// "C:\workshop" counting as inside "C:\work", the same trap
    /// <see cref="SessionAccessGrants"/> guards against.
    /// </summary>
    private static bool IsWithin(string path, string folder)
    {
        string full, root;

        try
        {
            full = Path.GetFullPath(path);
            root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
               || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether the URL is allowed by the domain allowlist.
    /// Empty allowlist means allow everything (with logging).
    /// </summary>
    public static bool IsUrlAllowed(
        string url,
        IReadOnlyList<string> allowedDomains,
        out string domain)
    {
        domain = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        try
        {
            var uri = new Uri(url);
            domain = uri.Host;

            // Empty allowlist means allow all
            if (allowedDomains.Count == 0)
                return true;

            // Check if domain or any parent domain is in the allowlist
            foreach (var allowed in allowedDomains)
            {
                if (domain.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith($".{allowed}", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (UriFormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates browser settings configuration. Returns error message if invalid.
    /// </summary>
    public static string? ValidateSettings(BrowserSettings settings)
    {
        if (!settings.Enabled)
            return null;

        // If not using isolated profile, require domain allowlist
        if (!settings.UseIsolatedProfile && settings.AllowedDomains.Count == 0)
        {
            return "When UseIsolatedProfile is false, AllowedDomains must contain at least one domain. " +
                   "Using an authenticated browser profile without a domain allowlist exposes all " +
                   "your logged-in accounts to any site the model visits.";
        }

        return null;
    }
}
