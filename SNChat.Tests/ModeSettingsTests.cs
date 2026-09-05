using SNChat.Core.Models;

namespace SNChat.Tests;

public class ModeSettingsTests
{
    private static ModeSettings Modes() => new()
    {
        ChatPrompt = "Be conversational.",
        CodingPrompt = "Prefer runnable code.",
        ScientificPrompt = "Carry units through."
    };

    [Theory]
    [InlineData(ChatMode.Chat, "Be conversational.")]
    [InlineData(ChatMode.Coding, "Prefer runnable code.")]
    [InlineData(ChatMode.Scientific, "Carry units through.")]
    public void PromptFor_returns_the_prompt_of_each_mode(string mode, string expected)
    {
        Assert.Equal(expected, Modes().PromptFor(mode));
    }

    [Fact]
    public void PromptFor_returns_empty_for_a_mode_that_does_not_exist()
    {
        // A hand-edited settings file can name anything; it should contribute
        // nothing rather than throw.
        Assert.Equal(string.Empty, Modes().PromptFor("Nonsense"));
    }

    [Fact]
    public void Mode_prompt_comes_before_the_template_prompt()
    {
        var combined = Modes().BuildSystemPrompt(ChatMode.Coding, "You are a reviewer.");

        Assert.Equal("Prefer runnable code.\n\nYou are a reviewer.", combined);
    }

    [Fact]
    public void Template_prompt_alone_is_used_when_the_mode_is_blank()
    {
        var modes = Modes();
        modes.CodingPrompt = string.Empty;

        Assert.Equal("You are a reviewer.",
            modes.BuildSystemPrompt(ChatMode.Coding, "You are a reviewer."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mode_prompt_alone_is_used_when_no_template_is_active(string? templatePrompt)
    {
        Assert.Equal("Prefer runnable code.",
            Modes().BuildSystemPrompt(ChatMode.Coding, templatePrompt));
    }

    [Fact]
    public void Nothing_at_all_yields_an_empty_prompt_so_no_system_message_is_sent()
    {
        var modes = new ModeSettings
        {
            ChatPrompt = string.Empty,
            CodingPrompt = string.Empty,
            ScientificPrompt = string.Empty
        };

        Assert.Equal(string.Empty, modes.BuildSystemPrompt(ChatMode.Chat, null));
    }

    [Fact]
    public void Surrounding_whitespace_does_not_widen_the_gap_between_the_two_parts()
    {
        var modes = Modes();
        modes.CodingPrompt = "  Prefer runnable code.\n\n";

        Assert.Equal("Prefer runnable code.\n\nYou are a reviewer.",
            modes.BuildSystemPrompt(ChatMode.Coding, "\n You are a reviewer.  "));
    }

    [Fact]
    public void Every_mode_ships_with_a_prompt_so_the_picker_is_useful_before_editing()
    {
        var defaults = new ModeSettings();

        foreach (var mode in ChatMode.All)
            Assert.False(string.IsNullOrWhiteSpace(defaults.PromptFor(mode)));
    }

    [Fact]
    public void Chat_is_the_mode_used_before_anything_has_been_picked()
    {
        Assert.Equal(ChatMode.Chat, new ModeSettings().LastMode);
    }
}
