using System.Collections.Concurrent;

namespace SNChat.Core.Services;

/// <summary>
/// Standing instructions kept as files rather than settings, so they can be
/// edited in a real editor, kept beside the code they describe, and committed
/// with it.
///
/// Two levels. A global RULES.md under the app's own folder applies everywhere;
/// a RULES.md in a project's root applies while working in that project, and is
/// the natural place for "this codebase uses tabs" or "never touch the
/// migrations folder".
///
/// Cached by write time because the system prompt is rebuilt whenever the
/// context meter refreshes - which is on every keystroke - and reading two files
/// that often would be absurd. Re-reading when the file changes means an edit
/// takes effect on the next message with no restart.
/// </summary>
public class RulesService
{
    /// <summary>
    /// The name looked for in a project root. Capitalised the way a person
    /// would write it; the lookup is case-insensitive on Windows anyway.
    /// </summary>
    public const string RulesFileName = "RULES.md";

    /// <summary>
    /// A ceiling on what one rules file may contribute. Rules go into every
    /// single request, so a large file is a permanent tax on the context window
    /// rather than a one-off cost, and someone will eventually paste an entire
    /// document in.
    /// </summary>
    public const int MaxCharacters = 8000;

    private readonly ConcurrentDictionary<string, (DateTime WrittenAt, string Text)> _cache = new();

    public string GlobalRulesPath { get; }

    public RulesService(string? globalRulesPath = null)
    {
        GlobalRulesPath = globalRulesPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SNChat", RulesFileName);
    }

    /// <summary>Rules that apply everywhere, or empty when there are none.</summary>
    public string ReadGlobal() => Read(GlobalRulesPath);

    /// <summary>
    /// Rules belonging to a project, or empty when the project has none or there
    /// is no project. Not an error: most folders will never have one.
    /// </summary>
    public string ReadForProject(string? projectRoot)
    {
        if (string.IsNullOrWhiteSpace(projectRoot))
            return string.Empty;

        try
        {
            return Read(Path.Combine(projectRoot, RulesFileName));
        }
        catch (ArgumentException)
        {
            // A malformed root, which a hand-edited project file can produce.
            return string.Empty;
        }
    }

    private string Read(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                _cache.TryRemove(path, out _);
                return string.Empty;
            }

            var writtenAt = File.GetLastWriteTimeUtc(path);

            if (_cache.TryGetValue(path, out var cached) && cached.WrittenAt == writtenAt)
                return cached.Text;

            var text = File.ReadAllText(path).Trim();

            if (text.Length > MaxCharacters)
            {
                text = text[..MaxCharacters]
                       + $"\n\n[rules truncated at {MaxCharacters} characters]";
            }

            _cache[path] = (writtenAt, text);
            return text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable rules must not stop a conversation. Losing them makes
            // the assistant less well briefed, not broken.
            return string.Empty;
        }
    }
}
