namespace SNChat.BuildTools;

/// <summary>
/// Decides whether the model is allowed to build in a given directory.
///
/// This is the whole security boundary for these tools, because a build runs
/// the project's own scripts - MSBuild targets, build.gradle, pre-build events
/// all execute arbitrary code. The model here reads web results and files from
/// MCP servers, so text it has been fed can try to talk it into building
/// something hostile. Confining it to directories the user named by hand means
/// the worst such an attempt can do is rebuild a project they already trust.
///
/// Nothing is allowed until roots are configured: an empty list turns the tools
/// off rather than opening everything.
/// </summary>
public sealed class WorkspaceGuard
{
    private readonly IReadOnlyList<string> _roots;

    public WorkspaceGuard(IEnumerable<string> allowedRoots)
    {
        _roots = allowedRoots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(Normalize)
            .Where(root => root.Length > 0)
            .ToList();
    }

    public bool HasRoots => _roots.Count > 0;

    public IReadOnlyList<string> Roots => _roots;

    /// <summary>
    /// The resolved full path when <paramref name="path"/> sits inside one of
    /// the allowed roots, or null when it does not.
    ///
    /// Resolved before comparing, so "C:\ok\..\..\Windows\System32" is judged as
    /// where it actually lands rather than as the string it was written as.
    /// </summary>
    public string? Resolve(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !HasRoots)
            return null;

        string full;

        try
        {
            // GetFullPath collapses "..", "." and mixed separators. It throws on
            // paths with characters the platform will not accept, which is a
            // rejection rather than something to propagate.
            full = Normalize(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return _roots.Any(root => IsWithin(full, root)) ? full : null;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> is the root itself or sits under it.
    ///
    /// The separator check is what stops "C:\workshop" from matching a root of
    /// "C:\work": comparing prefixes alone would treat any directory whose name
    /// merely starts with the root's as being inside it.
    /// </summary>
    private static bool IsWithin(string candidate, string root)
    {
        if (string.Equals(candidate, root, Comparison))
            return true;

        return candidate.StartsWith(root, Comparison)
            && candidate.Length > root.Length
            && candidate[root.Length] == Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// Windows paths are case-insensitive, so the comparison has to be too, or a
    /// root of "C:\Work" would reject "c:\work\app" - the same directory.
    /// </summary>
    private static StringComparison Comparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Trailing separators removed so a root written as "C:\work\" and one
    /// written as "C:\work" behave identically. A drive root such as "C:\" keeps
    /// its separator, since "C:" alone means something else entirely on Windows
    /// - the current directory of that drive.
    /// </summary>
    private static string Normalize(string path)
    {
        var trimmed = path.Trim();

        if (trimmed.Length == 0)
            return string.Empty;

        trimmed = trimmed.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

        if (trimmed.Length > 1
            && trimmed[^1] == Path.DirectorySeparatorChar
            && !(trimmed.Length == 3 && trimmed[1] == ':'))
        {
            trimmed = trimmed.TrimEnd(Path.DirectorySeparatorChar);
        }

        return trimmed;
    }

    /// <summary>Explains the refusal to the model, so it stops rather than retrying.</summary>
    public string DenialMessage(string path) => HasRoots
        ? $"'{path}' is outside the folders these tools may use. Allowed: " +
          string.Join("; ", _roots) + ". Add more under Settings - Build tools."
        : "No project folders have been allowed yet, so the build tools are off. " +
          "Add the folders you want the assistant to build under Settings - Build tools.";
}
