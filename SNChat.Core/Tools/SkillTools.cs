using System.Text;
using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Core.Tools;

/// <summary>
/// Skills are prompt templates the user has marked invocable, which turns a
/// personal collection of prompts into procedures the assistant can reach for
/// on its own.
///
/// Two tools rather than one per skill, deliberately. Every registered tool's
/// definition is sent on every request - the MCP tools already cost around
/// sixteen thousand tokens - so a tool per skill would make each new skill a
/// permanent charge against the context window. One pair costs the same whether
/// there are two skills or fifty.
/// </summary>
public class ListSkillsTool : ITool
{
    private readonly TemplateService _templates;

    public string Name => "list_skills";

    public string Description =>
        "List the named procedures ('skills') available for this assistant, with " +
        "what each is for. Call this when a task might have an established way of " +
        "being done here, then use_skill to read the one you want.";

    public ToolParameterSchema Parameters => new();

    public ListSkillsTool(TemplateService templates)
    {
        _templates = templates;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var skills = await SkillLookup.LoadInvocableAsync(_templates);

        if (skills.Count == 0)
        {
            return "No skills are available. A skill is a prompt template marked " +
                   "invocable in Settings, and none have been.";
        }

        var report = new StringBuilder();
        report.AppendLine($"{skills.Count} skill(s):");

        foreach (var skill in skills)
        {
            var description = string.IsNullOrWhiteSpace(skill.Description)
                ? "(no description)"
                : skill.Description;

            report.AppendLine($"  {skill.Name} — {description}");

            var variables = skill.GetVariables();

            if (variables.Count > 0)
                report.AppendLine($"      takes: {string.Join(", ", variables)}");
        }

        return report.ToString().TrimEnd();
    }
}

/// <summary>
/// Hands back a skill's instructions for the assistant to follow. The skill is
/// text, not code: what comes back is read and acted on, not executed.
/// </summary>
public class UseSkillTool : ITool
{
    private readonly TemplateService _templates;

    public string Name => "use_skill";

    public string Description =>
        "Read the instructions for a named skill and follow them. Call " +
        "list_skills first if you do not know what is available. Any " +
        "{{placeholders}} the skill declares should be supplied in 'arguments'.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["name"] = new()
            {
                Type = "string",
                Description = "The skill's name, exactly as list_skills reported it."
            },
            ["arguments"] = new()
            {
                Type = "object",
                Description = "Values for the skill's placeholders, as name/value pairs. " +
                              "Omit if the skill takes none."
            }
        },
        Required = new List<string> { "name" }
    };

    public UseSkillTool(TemplateService templates)
    {
        _templates = templates;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetValue("name", out var rawName) || rawName is null)
            return "Error: no 'name' argument was provided.";

        var name = rawName.ToString()?.Trim() ?? string.Empty;
        var skills = await SkillLookup.LoadInvocableAsync(_templates);

        var skill = skills.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

        if (skill == null)
        {
            var known = skills.Count == 0
                ? "There are no skills available."
                : "Available: " + string.Join(", ", skills.Select(s => s.Name));

            return $"There is no skill called '{name}'. {known}";
        }

        var values = ReadArguments(arguments);
        var missing = skill.GetVariables()
            .Where(v => !values.ContainsKey(v))
            .ToList();

        var report = new StringBuilder();
        report.AppendLine($"Instructions for the skill \"{skill.Name}\". Follow them.");

        // Said before the text rather than after, so it is read as a caveat on
        // what follows rather than as an afterthought. Unfilled placeholders are
        // left visible in the body on purpose.
        if (missing.Count > 0)
            report.AppendLine($"(No value was given for: {string.Join(", ", missing)}.)");

        report.AppendLine();
        report.AppendLine(skill.Render(values));

        if (!string.IsNullOrWhiteSpace(skill.SystemPrompt))
        {
            report.AppendLine();
            report.AppendLine("Also apply while doing this:");
            report.AppendLine(skill.RenderSystemPrompt(values));
        }

        return report.ToString().TrimEnd();
    }

    /// <summary>
    /// The placeholder values. Anything that is not an object is ignored rather
    /// than guessed at; the skill then reports which values it did not get.
    /// </summary>
    private static Dictionary<string, string> ReadArguments(
        IReadOnlyDictionary<string, object?> arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!arguments.TryGetValue("arguments", out var raw) || raw is null)
            return values;

        if (raw is IReadOnlyDictionary<string, object?> supplied)
        {
            foreach (var (key, value) in supplied)
                values[key] = value?.ToString() ?? string.Empty;
        }
        else if (raw is IDictionary<string, object?> mutable)
        {
            foreach (var (key, value) in mutable)
                values[key] = value?.ToString() ?? string.Empty;
        }

        return values;
    }
}

internal static class SkillLookup
{
    /// <summary>
    /// The invocable templates, in a stable order. A template that fails to
    /// parse is already skipped by the loader, so a broken file costs its own
    /// skill and no others.
    /// </summary>
    public static async Task<List<PromptTemplate>> LoadInvocableAsync(TemplateService templates)
    {
        var all = await templates.LoadAllAsync();

        return all
            .Where(t => t.Invocable && !string.IsNullOrWhiteSpace(t.Name))
            .OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
