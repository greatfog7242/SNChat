namespace SNChat.Core.Services;

/// <summary>
/// Folders the user has allowed the assistant to read during this session, and
/// folders they have refused.
///
/// Deliberately not saved. A grant is an answer to "may I read this, now, for
/// this" - it should not quietly still be in force next week when the reason has
/// been forgotten. Closing the app takes them all back, and that is the point:
/// the permanent decision is a project or an allowed root, made in Settings on
/// purpose, and this is the temporary one.
///
/// Refusals are remembered for the same session too, which matters more than it
/// sounds. Without that, an assistant working on its own would put the same
/// dialog in front of the user on every retry - and a question asked forty times
/// is answered carelessly the fortieth.
/// </summary>
public class SessionAccessGrants
{
    private readonly object _gate = new();
    private readonly List<string> _granted = new();
    private readonly List<string> _refused = new();

    /// <summary>Folders granted so far, most recent last.</summary>
    public IReadOnlyList<string> Granted
    {
        get { lock (_gate) return _granted.ToList(); }
    }

    /// <summary>
    /// What to ask about, given a path the assistant was refused.
    ///
    /// A file's folder rather than the file itself, because "open the log I
    /// mentioned" is followed by "and the one next to it", and asking once per
    /// file trains the user to click Yes without reading. The dialog names the
    /// folder plainly, so the wider grant is a visible choice rather than a
    /// quiet one.
    /// </summary>
    public static string? FolderToGrant(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        string full;

        try
        {
            full = Path.GetFullPath(path.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        // A directory is granted as itself; anything else is granted by its
        // parent. A path that does not exist yet is treated as a file, since
        // that is what "write this file" looks like.
        var folder = Directory.Exists(full) ? full : Path.GetDirectoryName(full);

        return string.IsNullOrEmpty(folder) ? null : folder;
    }

    /// <summary>
    /// Whether a folder may be granted at all.
    ///
    /// A drive root or a system folder is refused outright rather than put to
    /// the user. Granting C:\ would hand over every file on the machine in
    /// answer to a question about one of them, and a user reading a dialog
    /// about a file they asked for is in no position to notice that. If someone
    /// genuinely wants that, Settings is the place where the decision is
    /// deliberate.
    /// </summary>
    public static bool MayBeGranted(string folder, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(folder))
        {
            reason = "no folder was named";
            return false;
        }

        var full = Path.GetFullPath(folder);

        if (string.Equals(full, Path.GetPathRoot(full), StringComparison.OrdinalIgnoreCase))
        {
            reason = "it is the root of a whole drive";
            return false;
        }

        foreach (var special in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.System,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86
                 })
        {
            var protectedPath = Environment.GetFolderPath(special);

            if (protectedPath.Length > 0 && IsWithin(full, protectedPath))
            {
                reason = "it is inside a Windows system folder";
                return false;
            }
        }

        return true;
    }

    /// <summary>True when this path is already covered by a grant.</summary>
    public bool IsGranted(string path) => Matches(path, _granted);

    /// <summary>True when the user has already said no to this, this session.</summary>
    public bool IsRefused(string path) => Matches(path, _refused);

    /// <summary>
    /// One spelling per folder.
    ///
    /// Path.GetFullPath keeps a trailing separator, so "D:\work" and "D:\work\"
    /// come back different and are stored twice - which then appends the same
    /// folder to the server's command line twice.
    /// </summary>
    private static string Normalise(string folder)
    {
        var full = Path.GetFullPath(folder);

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // Except at a drive root, where the separator is part of the path.
        return trimmed.Length == 0 || trimmed.EndsWith(':') ? full : trimmed;
    }

    public void Grant(string folder)
    {
        lock (_gate)
        {
            var full = Normalise(folder);

            // A refusal that is later reconsidered should not keep blocking.
            _refused.RemoveAll(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase));

            if (!_granted.Any(g => string.Equals(g, full, StringComparison.OrdinalIgnoreCase)))
                _granted.Add(full);
        }
    }

    public void Refuse(string folder)
    {
        lock (_gate)
        {
            var full = Normalise(folder);

            if (!_refused.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
                _refused.Add(full);
        }
    }

    /// <summary>Forgets everything, as if the session had just started.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _granted.Clear();
            _refused.Clear();
        }
    }

    private bool Matches(string path, List<string> folders)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        string full;

        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        lock (_gate)
            return folders.Any(folder => IsWithin(full, folder));
    }

    /// <summary>
    /// Whether <paramref name="path"/> sits inside <paramref name="folder"/>.
    ///
    /// The separator check is what stops "C:\workshop" counting as inside
    /// "C:\work" - the same trap <see cref="Core"/>'s build guard was written to
    /// avoid, and it is just as wrong here.
    /// </summary>
    private static bool IsWithin(string path, string folder)
    {
        var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            return true;

        return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
