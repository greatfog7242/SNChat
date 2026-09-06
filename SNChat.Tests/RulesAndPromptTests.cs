using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Rules go into every request, so what they compose into - and what happens
/// when there are none - is the difference between briefing the assistant and
/// quietly changing how every conversation behaves.
/// </summary>
public class RulesAndPromptTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "snchat-rules-" + Guid.NewGuid().ToString("N")[..8]);

    public RulesAndPromptTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Global => Path.Combine(_directory, "global-RULES.md");

    private RulesService Service() => new(Global);

    private string ProjectWithRules(string name, string text)
    {
        var root = Path.Combine(_directory, name);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, RulesService.RulesFileName), text);
        return root;
    }

    // --- composition ---

    [Fact]
    public void With_no_rules_the_prompt_is_exactly_what_it_was_before_rules_existed()
    {
        // The regression that matters most: adding this feature must not change
        // a single existing conversation's behaviour.
        var composed = SystemPromptComposer.Compose(
            null, null, "Prefer runnable code.", "You are a reviewer.");

        Assert.Equal("Prefer runnable code.\n\nYou are a reviewer.", composed);
    }

    [Fact]
    public void Parts_run_from_most_general_to_most_specific()
    {
        // Where two instructions disagree, the later one is meant to win.
        var composed = SystemPromptComposer.Compose(
            "global", "project", "mode", "template");

        Assert.Equal("global\n\nproject\n\nmode\n\ntemplate", composed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_part_contributes_nothing_rather_than_a_blank_line(string? absent)
    {
        Assert.Equal("a\n\nb", SystemPromptComposer.Compose("a", absent, "b"));
    }

    [Fact]
    public void Nothing_at_all_means_no_system_message_is_sent()
    {
        Assert.Equal(string.Empty, SystemPromptComposer.Compose(null, "", "   "));
    }

    [Fact]
    public void Surrounding_whitespace_does_not_widen_the_gaps()
    {
        Assert.Equal("a\n\nb", SystemPromptComposer.Compose("  a\n\n", "\n b  "));
    }

    // --- reading ---

    [Fact]
    public void Global_rules_are_read_when_the_file_exists()
    {
        File.WriteAllText(Global, "Always explain your reasoning.");

        Assert.Equal("Always explain your reasoning.", Service().ReadGlobal());
    }

    [Fact]
    public void No_rules_file_is_ordinary_rather_than_an_error()
    {
        // Most folders will never have one.
        Assert.Equal(string.Empty, Service().ReadGlobal());
        Assert.Equal(string.Empty, Service().ReadForProject(_directory));
    }

    [Fact]
    public void Project_rules_are_read_from_the_project_root()
    {
        var root = ProjectWithRules("app", "This codebase uses tabs.");

        Assert.Equal("This codebase uses tabs.", Service().ReadForProject(root));
    }

    [Fact]
    public void With_no_project_there_are_no_project_rules()
    {
        Assert.Equal(string.Empty, Service().ReadForProject(null));
    }

    [Fact]
    public void An_edit_is_picked_up_without_a_restart()
    {
        // The read is cached by write time, since the prompt is rebuilt on every
        // keystroke. Caching must not mean an edited rules file is ignored.
        var service = Service();
        File.WriteAllText(Global, "first");
        Assert.Equal("first", service.ReadGlobal());

        // Written far enough apart that the timestamp certainly differs.
        File.SetLastWriteTimeUtc(Global, DateTime.UtcNow.AddSeconds(5));
        File.WriteAllText(Global, "second");
        File.SetLastWriteTimeUtc(Global, DateTime.UtcNow.AddSeconds(10));

        Assert.Equal("second", service.ReadGlobal());
    }

    [Fact]
    public void Deleting_the_rules_file_stops_the_rules_applying()
    {
        var service = Service();
        File.WriteAllText(Global, "temporary");
        Assert.Equal("temporary", service.ReadGlobal());

        File.Delete(Global);

        Assert.Equal(string.Empty, service.ReadGlobal());
    }

    [Fact]
    public void An_enormous_rules_file_is_truncated()
    {
        // Rules are charged against every request, so a pasted-in document is a
        // permanent tax rather than a one-off cost.
        File.WriteAllText(Global, new string('x', RulesService.MaxCharacters * 2));

        var read = Service().ReadGlobal();

        Assert.True(read.Length < RulesService.MaxCharacters + 200, $"length was {read.Length}");
        Assert.Contains("truncated", read);
    }

    [Fact]
    public void A_malformed_project_root_yields_no_rules_rather_than_throwing()
    {
        // Project files are hand-editable, so the root can be anything at all.
        Assert.Equal(string.Empty, Service().ReadForProject("\0not a path"));
    }

    // --- skills ---

    [Fact]
    public void A_template_is_not_invocable_unless_it_says_so()
    {
        // Templates written before skills existed carry no flag, and absent has
        // to mean "not offered to the model".
        Assert.False(new PromptTemplate().Invocable);
    }
}
