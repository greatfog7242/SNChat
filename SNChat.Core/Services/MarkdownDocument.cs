namespace SNChat.Core.Services;

/// <summary>
/// Splits a markdown file into its YAML frontmatter and its body.
///
/// Exists because doing this with <c>content.Split("---")</c> is wrong in a way
/// that loses data. That splits on the substring anywhere it appears, so any
/// value containing three hyphens tears the frontmatter in half - and YAML
/// writes a value containing a newline as a folded scalar, putting its content
/// on following lines where a bare "---" is entirely legitimate.
///
/// This was not hypothetical. Conversations whose first message was an
/// attachment got the title "--- Attached image: photo.jpg ---", which was
/// written correctly and then could never be read back: the frontmatter parsed
/// as far as the first three hyphens, "created" went missing, and the whole
/// conversation failed to load. Several were sitting unreadable on disk.
///
/// A delimiter is a line that is exactly three hyphens, nothing else.
/// </summary>
public static class MarkdownDocument
{
    private const string Delimiter = "---";

    /// <summary>
    /// True when the content opens with a frontmatter block. The frontmatter is
    /// returned without its delimiters, and the body is everything after the
    /// closing one, with any leading blank line removed.
    /// </summary>
    public static bool TrySplit(string content, out string frontmatter, out string body)
    {
        frontmatter = string.Empty;
        body = string.Empty;

        if (string.IsNullOrEmpty(content))
            return false;

        // Kept as an array rather than a reader so the body can be rejoined
        // exactly as it was, including its blank lines.
        var lines = content.Replace("\r\n", "\n").Split('\n');

        var opening = -1;

        for (var i = 0; i < lines.Length; i++)
        {
            // A byte order mark can sit before the first delimiter.
            var line = lines[i].TrimStart('﻿').TrimEnd();

            if (line.Length == 0)
                continue;

            if (IsDelimiter(line))
                opening = i;

            // The first non-blank line decides it either way: content that does
            // not open with a delimiter has no frontmatter.
            break;
        }

        if (opening < 0)
            return false;

        for (var i = opening + 1; i < lines.Length; i++)
        {
            if (!IsDelimiter(lines[i].TrimEnd()))
                continue;

            frontmatter = string.Join("\n", lines[(opening + 1)..i]).Trim();
            body = string.Join("\n", lines[(i + 1)..]).TrimStart('\n');
            return true;
        }

        // Opened but never closed.
        return false;
    }

    /// <summary>
    /// A delimiter sits at column zero, with nothing before it.
    ///
    /// The indentation matters, and trimming both ends was not enough. YAML
    /// writes a multi-line value as a block scalar and indents its content by
    /// two spaces, so a system prompt or a title containing three hyphens
    /// produces a line reading "  ---" - which is content, not a delimiter.
    /// Treating it as one truncated the frontmatter in a subtler version of the
    /// very bug this class exists to fix.
    /// </summary>
    private static bool IsDelimiter(string line) => line == Delimiter;
}
