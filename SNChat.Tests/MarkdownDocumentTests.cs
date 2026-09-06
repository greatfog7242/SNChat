using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Splitting frontmatter from body used to be done with content.Split("---"),
/// which splits on the substring anywhere it appears. That is not a tidiness
/// problem: several real conversations on disk were written correctly and could
/// never be read back, because their title contained three hyphens.
/// </summary>
public class MarkdownDocumentTests
{
    [Fact]
    public void An_ordinary_document_splits_into_frontmatter_and_body()
    {
        var split = MarkdownDocument.TrySplit("""
            ---
            id: 1
            title: Hello
            ---

            # Hello

            Body text.
            """, out var frontmatter, out var body);

        Assert.True(split);
        Assert.Contains("title: Hello", frontmatter);
        Assert.StartsWith("# Hello", body);
        Assert.Contains("Body text.", body);
    }

    /// <summary>
    /// The case that was losing conversations. A first message carrying an
    /// attachment produces the title "--- Attached image: photo.jpg ---", which
    /// YAML then writes as a folded scalar across several lines.
    /// </summary>
    [Fact]
    public void A_title_containing_three_hyphens_no_longer_destroys_the_frontmatter()
    {
        var split = MarkdownDocument.TrySplit("""
            ---
            id: 7942a480-3ad1-43c0-8b92-1f919b893591
            title: >-
              --- Attached image: 20251030_170533.jpg ---
            created: 2026-08-31T00:58:47.5576118Z
            ---

            # Something

            ## Message 1 (User) - 2026-08-31 00:58:47
            hello
            """, out var frontmatter, out var body);

        Assert.True(split);

        // The whole point: everything after the offending title survives.
        Assert.Contains("created:", frontmatter);
        Assert.Contains("Attached image", frontmatter);
        Assert.Contains("## Message 1", body);
    }

    [Fact]
    public void An_indented_three_hyphens_is_content_not_a_delimiter()
    {
        // The subtler half of the same bug. YAML indents a block scalar's
        // content by two spaces, so a value containing three hyphens produces a
        // line reading "  ---". Trimming both ends before comparing treats that
        // as the closing delimiter and truncates the frontmatter.
        var split = MarkdownDocument.TrySplit("""
            ---
            title: >-
              ---
              still the title
            created: 2026-09-06
            ---

            body
            """, out var frontmatter, out var body);

        Assert.True(split);
        Assert.Contains("still the title", frontmatter);
        Assert.Contains("created:", frontmatter);
        Assert.Equal("body", body.Trim());
    }

    [Fact]
    public void Three_hyphens_inside_a_line_are_not_a_delimiter()
    {
        var split = MarkdownDocument.TrySplit("""
            ---
            title: Fix the a---b bug
            created: 2026-09-06
            ---

            body
            """, out var frontmatter, out var body);

        Assert.True(split);
        Assert.Contains("created:", frontmatter);
        Assert.Equal("body", body.Trim());
    }

    [Fact]
    public void A_horizontal_rule_in_the_body_is_left_alone()
    {
        MarkdownDocument.TrySplit("""
            ---
            title: T
            ---

            before

            ---

            after
            """, out _, out var body);

        Assert.Contains("before", body);
        Assert.Contains("after", body);
        Assert.Contains("---", body);
    }

    [Fact]
    public void Content_with_no_frontmatter_is_refused_rather_than_guessed_at()
    {
        Assert.False(MarkdownDocument.TrySplit("# Just a heading\n\ntext", out _, out _));
    }

    [Fact]
    public void Frontmatter_that_is_opened_and_never_closed_is_refused()
    {
        Assert.False(MarkdownDocument.TrySplit("---\nid: 1\nno closing delimiter", out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_at_all_is_refused(string content)
    {
        Assert.False(MarkdownDocument.TrySplit(content, out _, out _));
    }

    [Fact]
    public void Windows_line_endings_are_handled()
    {
        // Every file this reads was written on Windows.
        var split = MarkdownDocument.TrySplit(
            "---\r\nid: 1\r\ntitle: T\r\n---\r\n\r\nbody\r\n", out var frontmatter, out var body);

        Assert.True(split);
        Assert.Contains("title: T", frontmatter);
        Assert.Equal("body", body.Trim());
    }

    [Fact]
    public void A_byte_order_mark_before_the_first_delimiter_is_tolerated()
    {
        var split = MarkdownDocument.TrySplit("﻿---\nid: 1\n---\n\nbody", out var frontmatter, out _);

        Assert.True(split);
        Assert.Contains("id: 1", frontmatter);
    }

    /// <summary>
    /// A conversation's system prompt is user-written and multi-line, which is
    /// exactly the shape that used to tear the file in half. YAML writes it as a
    /// folded scalar, so the delimiter check has to be line-based for it to be
    /// safe to store there at all.
    /// </summary>
    [Fact]
    public void A_multi_line_system_prompt_round_trips_through_the_frontmatter()
    {
        var serializer = new YamlDotNet.Serialization.SerializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .Build();

        var prompt = "Be careful.\n---\nThat line is a horizontal rule, not a delimiter.";

        var file = "---\n"
                   + serializer.Serialize(new { id = 1, system_prompt = prompt }).TrimEnd()
                   + "\n---\n\n## Message 1 (User) - 2026-09-06 10:00:00\nhello\n";

        var split = MarkdownDocument.TrySplit(file, out var frontmatter, out var body);

        Assert.True(split);
        Assert.Contains("horizontal rule", frontmatter);
        Assert.Contains("## Message 1", body);

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
            .Build();

        var read = deserializer.Deserialize<Dictionary<string, object>>(frontmatter);

        Assert.Contains("horizontal rule", read["system_prompt"].ToString());
    }

    [Fact]
    public void An_empty_body_is_fine()
    {
        var split = MarkdownDocument.TrySplit("---\nid: 1\n---\n", out _, out var body);

        Assert.True(split);
        Assert.Equal(string.Empty, body.Trim());
    }
}
