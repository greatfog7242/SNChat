using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Cached pictures are written into the conversation's attachments folder and
/// recorded as a relative link, so a conversation folder stays portable. The
/// renderer cannot resolve those links, so they are expanded to absolute file
/// URIs on load and reduced again on save. Getting either direction wrong makes
/// every cached image vanish, so both are pinned here.
/// </summary>
public class ConversationPathsTests : IDisposable
{
    private readonly string _conversationDirectory;
    private readonly string _attachmentsDirectory;

    public ConversationPathsTests()
    {
        _conversationDirectory = Path.Combine(
            Path.GetTempPath(), "snchat-tests", Guid.NewGuid().ToString());

        _attachmentsDirectory = Path.Combine(
            _conversationDirectory, ConversationPaths.AttachmentsFolderName);

        Directory.CreateDirectory(_attachmentsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_conversationDirectory))
            Directory.Delete(_conversationDirectory, recursive: true);
    }

    private string WriteAttachment(string name)
    {
        var path = Path.Combine(_attachmentsDirectory, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void A_stored_link_becomes_a_file_uri_the_renderer_can_load()
    {
        var path = WriteAttachment("web-1a2b3c.jpg");

        var resolved = ConversationPaths.ResolveForDisplay(
            "![A red panda](attachments/web-1a2b3c.jpg)", _conversationDirectory);

        Assert.Equal($"![A red panda]({new Uri(path).AbsoluteUri})", resolved);
    }

    [Fact]
    public void An_absolute_link_is_stored_relative_so_the_folder_can_move()
    {
        var path = WriteAttachment("web-1a2b3c.jpg");
        var markdown = $"![A red panda]({new Uri(path).AbsoluteUri})";

        Assert.Equal(
            "![A red panda](attachments/web-1a2b3c.jpg)",
            ConversationPaths.ReduceForStorage(markdown, _conversationDirectory));
    }

    [Fact]
    public void Saving_then_loading_leaves_the_link_unchanged()
    {
        WriteAttachment("web-1a2b3c.jpg");
        const string stored = "![A red panda](attachments/web-1a2b3c.jpg)";

        var roundTripped = ConversationPaths.ReduceForStorage(
            ConversationPaths.ResolveForDisplay(stored, _conversationDirectory),
            _conversationDirectory);

        Assert.Equal(stored, roundTripped);
    }

    [Fact]
    public void A_picture_that_was_never_cached_keeps_its_web_address()
    {
        const string markdown = "![A red panda](https://example.com/panda.jpg)";

        Assert.Equal(markdown,
            ConversationPaths.ResolveForDisplay(markdown, _conversationDirectory));
        Assert.Equal(markdown,
            ConversationPaths.ReduceForStorage(markdown, _conversationDirectory));
    }

    /// <summary>
    /// A cached file deleted by hand must not be rewritten into an absolute path
    /// that does not exist, which would render as nothing at all.
    /// </summary>
    [Fact]
    public void A_link_to_a_missing_file_is_left_alone()
    {
        const string markdown = "![Gone](attachments/web-deleted.jpg)";

        Assert.Equal(markdown,
            ConversationPaths.ResolveForDisplay(markdown, _conversationDirectory));
    }

    /// <summary>
    /// Only this conversation's own folder is relativised. A user-attached image
    /// living elsewhere on disk has no relative form and must survive a save.
    /// </summary>
    [Fact]
    public void An_image_outside_the_conversation_folder_stays_absolute()
    {
        var outside = Path.Combine(Path.GetTempPath(), "elsewhere.png");
        var markdown = $"![Elsewhere]({new Uri(outside).AbsoluteUri})";

        Assert.Equal(markdown,
            ConversationPaths.ReduceForStorage(markdown, _conversationDirectory));
    }

    [Fact]
    public void Every_picture_in_a_reply_is_converted()
    {
        WriteAttachment("web-one.jpg");
        WriteAttachment("web-two.png");

        var resolved = ConversationPaths.ResolveForDisplay(
            "![One](attachments/web-one.jpg)\ntext\n![Two](attachments/web-two.png)",
            _conversationDirectory);

        Assert.DoesNotContain("(attachments/", resolved);
        Assert.Equal(2, resolved.Split("file:///").Length - 1);
    }

    /// <summary>
    /// Wikimedia filenames routinely contain spaces and punctuation, and a link
    /// target is only matched when it has neither brackets nor whitespace.
    /// </summary>
    [Fact]
    public void A_link_target_containing_spaces_is_not_mangled()
    {
        const string markdown = "![Odd](attachments/two words.jpg)";

        Assert.Equal(markdown,
            ConversationPaths.ResolveForDisplay(markdown, _conversationDirectory));
    }
}
