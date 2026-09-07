using System.Text.RegularExpressions;

namespace SNChat.Core.Services;

/// <summary>
/// Recognises a filesystem MCP server refusing a path, so the app can offer to
/// widen the permission instead of leaving the assistant stuck.
///
/// Matched against the real thing rather than an invented shape. Probed from
/// @modelcontextprotocol/server-filesystem 0.2.0, reading a file outside its
/// allowed directories returns isError with:
///
///   Access denied - path outside allowed directories: D:\a\b.md not in C:\ai-playground
///
/// Only the prefix and the " not in " separator are relied on; both parts are
/// taken as-is, since a Windows path can contain almost anything including
/// spaces and, as this repository's own path proves, a '#'.
/// </summary>
public static class McpAccessDenial
{
    /// <summary>
    /// Non-greedy up to the first " not in ", because the refused path may
    /// itself contain the words. The allowed roots are comma-separated when
    /// there are several, and are captured whole for the message to the user.
    /// </summary>
    private static readonly Regex Pattern = new(
        @"Access denied\s*-\s*path outside allowed directories:\s*(?<path>.+?)\s+not in\s*(?<allowed>.*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>
    /// The path that was refused, or null when this is some other failure.
    /// A tool failing for an ordinary reason must not be mistaken for one
    /// asking for permission.
    /// </summary>
    public static bool TryParse(string? toolResult, out string refusedPath, out string allowedRoots)
    {
        refusedPath = string.Empty;
        allowedRoots = string.Empty;

        if (string.IsNullOrWhiteSpace(toolResult))
            return false;

        var match = Pattern.Match(toolResult);

        if (!match.Success)
            return false;

        refusedPath = match.Groups["path"].Value.Trim();
        allowedRoots = match.Groups["allowed"].Value.Trim();

        return refusedPath.Length > 0;
    }
}
