using System.Text.RegularExpressions;

namespace SNChat.Core.Services;

/// <summary>
/// Translates attachment links between the two forms they need to take.
///
/// On disk a conversation records "attachments/web-1a2b.jpg", so the folder can
/// be moved, copied to another machine, or opened under a different user profile
/// without every picture breaking. In memory the same link has to be an absolute
/// file URI, because the markdown renderer resolves image sources against the
/// application's base directory rather than the conversation's.
/// </summary>
public static partial class ConversationPaths
{
    public const string AttachmentsFolderName = "attachments";

    /// <summary>Relative, portable form written into conversation.md.</summary>
    public static string ToStoredPath(string absolutePath) =>
        $"{AttachmentsFolderName}/{Path.GetFileName(absolutePath)}";

    /// <summary>Absolute file URI the markdown renderer can load.</summary>
    public static string ToDisplayUri(string absolutePath) =>
        new Uri(absolutePath).AbsoluteUri;

    /// <summary>
    /// Rewrites every "attachments/..." image link to an absolute file URI under
    /// the given conversation folder. Links to files that are not there are left
    /// as they are, so a missing picture stays visible as a broken link instead
    /// of being silently dropped.
    /// </summary>
    public static string ResolveForDisplay(string markdown, string conversationDirectory)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        return AttachmentLink().Replace(markdown, match =>
        {
            var path = Path.Combine(
                conversationDirectory, AttachmentsFolderName, match.Groups["file"].Value);

            return File.Exists(path)
                ? $"![{match.Groups["alt"].Value}]({ToDisplayUri(path)})"
                : match.Value;
        });
    }

    /// <summary>
    /// The inverse of <see cref="ResolveForDisplay"/>: turns absolute file URIs
    /// that live in this conversation's attachments folder back into relative
    /// links. Images elsewhere on disk, and remote URLs, are left untouched.
    /// </summary>
    public static string ReduceForStorage(string markdown, string conversationDirectory)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        var attachments = Path.GetFullPath(
            Path.Combine(conversationDirectory, AttachmentsFolderName));

        return FileUriLink().Replace(markdown, match =>
        {
            var uri = match.Groups["uri"].Value;

            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile)
                return match.Value;

            var path = Path.GetFullPath(parsed.LocalPath);
            var directory = Path.GetDirectoryName(path);

            return string.Equals(directory, attachments, StringComparison.OrdinalIgnoreCase)
                ? $"![{match.Groups["alt"].Value}]({ToStoredPath(path)})"
                : match.Value;
        });
    }

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\(attachments/(?<file>[^()\s/]+)\)")]
    private static partial Regex AttachmentLink();

    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<uri>file:///[^()\s]+)\)")]
    private static partial Regex FileUriLink();
}
