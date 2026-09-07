using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// What a dropped file looks like to the model.
///
/// The image case earned its own tests on 2026-09-07: a dropped picture was
/// being delivered to the model correctly, and the model then called
/// read_media_file on the filename in the label anyway. The file server resolved
/// that bare name against its own root and answered ENOENT, so the user saw a
/// tool error on a feature that was working.
/// </summary>
public class AttachmentContextBlockTests
{
    private static Attachment Image(string name = "photo.jpg") => new()
    {
        FileName = name,
        Type = AttachmentType.Image,
        MimeType = "image/jpeg"
    };

    [Fact]
    public void Nothing_attached_contributes_nothing()
    {
        Assert.Equal(string.Empty, AttachmentService.BuildContextBlock(Array.Empty<Attachment>()));
    }

    [Fact]
    public void An_image_reaches_the_model_without_its_filename()
    {
        // The name is what the model chases. Told "do not open the file" and
        // given the name anyway, this model called read_media_file on it, then
        // fed it to image_search. Nothing to chase is the only version that held.
        var block = AttachmentService.BuildContextBlock(new[] { Image() });

        Assert.DoesNotContain("photo.jpg", block);
        Assert.DoesNotContain(".jpg", block);
        Assert.Contains("Attached image", block);
    }

    [Fact]
    public void An_image_says_there_is_no_file_to_open()
    {
        // The copy lives in the conversation's own folder, which is on no
        // allowed list, so a read can only ever fail.
        var block = AttachmentService.BuildContextBlock(new[] { Image() });

        Assert.Contains("no file and no filename", block);
    }

    [Fact]
    public void Several_images_are_numbered_so_they_can_be_told_apart()
    {
        // "The second one" has to mean something when three are dropped at once.
        var block = AttachmentService.BuildContextBlock(new[]
        {
            Image("a.jpg"), Image("b.png"), Image("c.png")
        });

        Assert.Contains("Attached image 1 of 3", block);
        Assert.Contains("Attached image 3 of 3", block);
        Assert.DoesNotContain("b.png", block);
    }

    [Fact]
    public void One_image_is_not_numbered()
    {
        Assert.Contains("--- Attached image ---",
            AttachmentService.BuildContextBlock(new[] { Image() }));
    }

    [Fact]
    public void An_image_is_not_told_whether_it_can_be_seen()
    {
        // A vision model gets the picture; a text-only one does not. Saying
        // either would be a lie to half the models this runs against.
        var block = AttachmentService.BuildContextBlock(new[] { Image() });

        Assert.DoesNotContain("you can see", block, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cannot see", block, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_text_file_still_arrives_as_its_contents()
    {
        var block = AttachmentService.BuildContextBlock(new[]
        {
            new Attachment
            {
                FileName = "notes.md",
                Type = AttachmentType.Document,
                ExtractedText = "the actual words"
            }
        });

        // A text file keeps its name: its contents are right there, so there is
        // nothing for the model to go hunting for.
        Assert.Contains("notes.md", block);
        Assert.Contains("the actual words", block);
    }

    [Fact]
    public void An_unreadable_file_is_still_told_not_to_be_guessed_at()
    {
        var block = AttachmentService.BuildContextBlock(new[]
        {
            new Attachment { FileName = "report.pdf", Type = AttachmentType.Other, FileSize = 2048 }
        });

        Assert.Contains("Do not guess", block);
    }
}
