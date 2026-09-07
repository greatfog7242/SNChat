using Microsoft.Extensions.Logging.Abstractions;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.Tests;

/// <summary>
/// A skill is a prompt template the user marked invocable, which lets the
/// assistant reach for an established way of doing something instead of
/// improvising it. What comes back is instructions to follow, not code to run.
/// </summary>
public class SkillToolsTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "snchat-skills-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly TemplateService _templates;

    public SkillToolsTests()
    {
        Directory.CreateDirectory(_directory);
        _templates = new TemplateService(NullLogger<TemplateService>.Instance, _directory);
    }

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

    private async Task<PromptTemplate> Add(
        string name, string content, bool invocable, string description = "", string systemPrompt = "")
    {
        var template = new PromptTemplate
        {
            Name = name,
            Content = content,
            Description = description,
            SystemPrompt = systemPrompt,
            Invocable = invocable
        };

        await _templates.SaveAsync(template);
        return template;
    }

    private Task<string> List() =>
        new ListSkillsTool(_templates).ExecuteAsync(new Dictionary<string, object?>());

    private Task<string> Use(string name, Dictionary<string, object?>? arguments = null) =>
        new UseSkillTool(_templates).ExecuteAsync(new Dictionary<string, object?>
        {
            ["name"] = name,
            ["arguments"] = arguments
        });

    [Fact]
    public async Task Only_templates_marked_invocable_are_offered_as_skills()
    {
        // Every skill is named in a tool description sent on every request, so
        // a personal collection of prompts must not become a standing cost.
        await Add("Release checklist", "Do the release.", invocable: true);
        await Add("Personal note", "Something private.", invocable: false);

        var listed = await List();

        Assert.Contains("Release checklist", listed);
        Assert.DoesNotContain("Personal note", listed);
    }

    [Fact]
    public async Task With_no_skills_the_answer_says_how_to_make_one()
    {
        await Add("Not a skill", "text", invocable: false);

        var listed = await List();

        Assert.Contains("No skills are available", listed);
        Assert.Contains("invocable", listed);
    }

    [Fact]
    public async Task The_listing_reports_what_a_skill_takes()
    {
        await Add("Review", "Review this {{language}} code:\n{{code}}",
            invocable: true, description: "Careful review.");

        var listed = await List();

        Assert.Contains("Careful review.", listed);
        Assert.Contains("language", listed);
        Assert.Contains("code", listed);
    }

    [Fact]
    public async Task Using_a_skill_returns_its_instructions_with_values_filled_in()
    {
        await Add("Review", "Review this {{language}} code carefully.", invocable: true);

        var used = await Use("Review", new Dictionary<string, object?> { ["language"] = "Kotlin" });

        Assert.Contains("Review this Kotlin code carefully.", used);
        Assert.Contains("Follow them", used);
    }

    [Fact]
    public async Task A_skills_system_prompt_comes_back_too()
    {
        await Add("Review", "Review it.", invocable: true,
            systemPrompt: "Prioritise correctness over style.");

        Assert.Contains("Prioritise correctness over style.", await Use("Review"));
    }

    [Fact]
    public async Task A_missing_value_is_reported_and_the_placeholder_left_visible()
    {
        // Silently substituting an empty string would leave the model following
        // instructions with a hole in them and no idea.
        await Add("Review", "Review this {{language}} code.", invocable: true);

        var used = await Use("Review");

        Assert.Contains("No value was given for: language", used);
        Assert.Contains("{{language}}", used);
    }

    [Fact]
    public async Task Asking_for_an_unknown_skill_says_what_does_exist()
    {
        // So the model corrects itself instead of guessing again.
        await Add("Release checklist", "Do the release.", invocable: true);

        var used = await Use("Deploy");

        Assert.Contains("no skill called 'Deploy'", used);
        Assert.Contains("Release checklist", used);
    }

    [Fact]
    public async Task A_skill_name_is_matched_regardless_of_case()
    {
        await Add("Release Checklist", "Do the release.", invocable: true);

        Assert.Contains("Do the release.", await Use("release checklist"));
    }

    [Fact]
    public async Task Asking_with_no_name_is_reported_rather_than_guessed_at()
    {
        var used = await new UseSkillTool(_templates)
            .ExecuteAsync(new Dictionary<string, object?>());

        Assert.Contains("no 'name' argument", used);
    }

    [Fact]
    public async Task Invocable_survives_being_written_and_read_back()
    {
        var saved = await Add("Skill", "text", invocable: true);

        var reloaded = (await _templates.LoadAllAsync())
            .Single(t => t.Id == saved.Id);

        Assert.True(reloaded.Invocable);
    }

    [Fact]
    public async Task The_startup_check_agrees_with_what_the_tools_find()
    {
        // Registration is decided at startup by a cheap scan rather than a full
        // parse; if the two disagree the tools are offered when there is nothing
        // to list, or withheld when there is.
        Assert.False(_templates.HasInvocableTemplates());

        await Add("Skill", "text", invocable: true);

        Assert.True(_templates.HasInvocableTemplates());
    }
}
