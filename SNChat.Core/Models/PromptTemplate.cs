using System.Text.RegularExpressions;

namespace SNChat.Core.Models;

/// <summary>
/// A reusable prompt with optional {{placeholders}} filled in at use time.
/// </summary>
public partial class PromptTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = "General";

    /// <summary>The prompt body, which may contain {{variable}} placeholders.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Optional system prompt applied to the conversation when this template is
    /// used, so a template can set the assistant's role as well as the message.
    /// </summary>
    public string SystemPrompt { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Path on disk; null for a template that has not been saved yet.</summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Distinct placeholder names in the order they first appear, so the fill-in
    /// form matches the order the user reads them in the prompt.
    /// </summary>
    public IReadOnlyList<string> GetVariables()
    {
        var names = new List<string>();

        foreach (Match match in VariableRegex().Matches(Content + " " + SystemPrompt))
        {
            var name = match.Groups["name"].Value.Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                names.Add(name);
        }

        return names;
    }

    /// <summary>
    /// Substitutes values into the placeholders. Names are matched
    /// case-insensitively; any placeholder without a value is left untouched so
    /// the omission is visible rather than silently becoming an empty string.
    /// </summary>
    public string Render(IReadOnlyDictionary<string, string> values) =>
        Substitute(Content, values);

    public string RenderSystemPrompt(IReadOnlyDictionary<string, string> values) =>
        Substitute(SystemPrompt, values);

    private static string Substitute(string text, IReadOnlyDictionary<string, string> values)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return VariableRegex().Replace(text, match =>
        {
            var name = match.Groups["name"].Value.Trim();

            foreach (var pair in values)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                    return pair.Value;
            }

            return match.Value;
        });
    }

    /// <summary>
    /// Double braces are used rather than single so that prompts containing JSON
    /// or code samples do not get mangled.
    /// </summary>
    [GeneratedRegex(@"\{\{(?<name>[^{}]+)\}\}")]
    private static partial Regex VariableRegex();
}
