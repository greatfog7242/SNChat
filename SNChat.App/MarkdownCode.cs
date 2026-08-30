using System.Text;
using System.Text.RegularExpressions;

namespace SNChat.App;

/// <summary>
/// Pulls fenced code blocks out of markdown so they can be copied without the
/// surrounding prose. Markdig renders a code block as one flat run of text with
/// no language attached, so the source markdown is parsed here instead.
/// </summary>
public static partial class MarkdownCode
{
    /// <summary>True when the text contains at least one fenced code block.</summary>
    public static bool HasBlocks(string? markdown) =>
        !string.IsNullOrEmpty(markdown) && FenceRegex().IsMatch(markdown);

    /// <summary>
    /// Returns the contents of every fenced block, concatenated and separated by
    /// a blank line. Returns an empty string when there are none.
    /// </summary>
    public static string ExtractBlocks(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
            return string.Empty;

        var blocks = new List<string>();

        foreach (Match match in FenceRegex().Matches(markdown))
        {
            var code = match.Groups["code"].Value.TrimEnd();
            if (!string.IsNullOrWhiteSpace(code))
                blocks.Add(code);
        }

        if (blocks.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (i > 0)
                sb.AppendLine();

            sb.AppendLine(blocks[i]);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Matches ```lang\n ... \n``` including the optional language hint. Uses a
    /// lazy body so consecutive blocks are captured separately.
    /// </summary>
    [GeneratedRegex(@"```[^\r\n]*\r?\n(?<code>.*?)```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();
}
