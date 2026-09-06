using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SNChat.Core.Services;

/// <summary>
/// Loads and saves prompt templates as markdown files with YAML frontmatter,
/// matching how conversations are stored so templates stay hand-editable.
/// </summary>
public class TemplateService
{
    private readonly string _templatesDirectory;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ILogger<TemplateService> _logger;

    /// <summary>
    /// <paramref name="templatesDirectory"/> exists so tests can point this at a
    /// temporary folder. Left null everywhere else, which uses the real location
    /// under AppData - a service that can only ever write to the user's own
    /// profile cannot be tested without polluting it.
    /// </summary>
    public TemplateService(ILogger<TemplateService> logger, string? templatesDirectory = null)
    {
        _logger = logger;

        _templatesDirectory = templatesDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SNChat", "templates");

        Directory.CreateDirectory(_templatesDirectory);

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public string TemplatesDirectory => _templatesDirectory;

    /// <summary>
    /// Whether any template is marked invocable, without fully parsing them all.
    /// Used at startup to decide whether the skill tools are worth registering,
    /// which has to happen before anything async has run.
    ///
    /// Reads the files looking for the frontmatter flag rather than deserialising
    /// them, since the answer is usually no and the cost should match.
    /// </summary>
    public bool HasInvocableTemplates()
    {
        try
        {
            if (!Directory.Exists(_templatesDirectory))
                return false;

            foreach (var file in Directory.EnumerateFiles(_templatesDirectory, "*.md"))
            {
                foreach (var line in File.ReadLines(file))
                {
                    // Only the frontmatter can carry it, and that ends at the
                    // second delimiter - stop rather than scan a whole body.
                    if (line.StartsWith("---", StringComparison.Ordinal) && line.Trim() == "---")
                        continue;

                    if (line.StartsWith("invocable:", StringComparison.OrdinalIgnoreCase)
                        && line.Contains("true", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Reads every template on disk. A file that fails to parse is skipped and
    /// logged rather than aborting the whole load, so one bad hand-edit does not
    /// hide the rest.
    /// </summary>
    public async Task<List<PromptTemplate>> LoadAllAsync()
    {
        var templates = new List<PromptTemplate>();

        if (!Directory.Exists(_templatesDirectory))
            return templates;

        foreach (var file in Directory.GetFiles(_templatesDirectory, "*.md"))
        {
            try
            {
                var template = await LoadAsync(file);
                if (template != null)
                    templates.Add(template);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable template {File}", file);
            }
        }

        return templates
            .OrderBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<PromptTemplate?> LoadAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var content = await File.ReadAllTextAsync(filePath);
        return Parse(content, filePath);
    }

    public async Task SaveAsync(PromptTemplate template)
    {
        template.UpdatedAt = DateTime.UtcNow;
        template.FilePath ??= Path.Combine(_templatesDirectory, BuildFileName(template));

        await File.WriteAllTextAsync(template.FilePath, Serialize(template));
        _logger.LogInformation("Saved template {Name}", template.Name);
    }

    public Task DeleteAsync(PromptTemplate template)
    {
        if (!string.IsNullOrEmpty(template.FilePath) && File.Exists(template.FilePath))
        {
            File.Delete(template.FilePath);
            _logger.LogInformation("Deleted template {Name}", template.Name);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes a small starter set the first time the folder is empty, so the
    /// feature is usable without having to author a template first.
    /// </summary>
    public async Task SeedDefaultsIfEmptyAsync()
    {
        if (Directory.GetFiles(_templatesDirectory, "*.md").Length > 0)
            return;

        foreach (var template in BuildDefaults())
            await SaveAsync(template);

        _logger.LogInformation("Seeded default prompt templates");
    }

    private static List<PromptTemplate> BuildDefaults() => new()
    {
        new PromptTemplate
        {
            Name = "Explain code",
            Category = "Code",
            Description = "Walk through what a piece of code does.",
            Content = "Explain what this {{language}} code does, step by step. " +
                      "Call out anything surprising or likely to be a bug.\n\n" +
                      "```{{language}}\n{{code}}\n```"
        },
        new PromptTemplate
        {
            Name = "Review code",
            Category = "Code",
            Description = "Critical review focused on correctness.",
            SystemPrompt = "You are a careful code reviewer. Prioritise correctness " +
                           "bugs over style. Say plainly when something is fine.",
            Content = "Review this {{language}} code. Focus on correctness, edge cases, " +
                      "and error handling.\n\n```{{language}}\n{{code}}\n```"
        },
        new PromptTemplate
        {
            Name = "Summarise text",
            Category = "Writing",
            Description = "Condense text to its key points.",
            Content = "Summarise the following in {{length}}. Keep the specific " +
                      "details and numbers; drop the filler.\n\n{{text}}"
        },
        new PromptTemplate
        {
            Name = "Translate",
            Category = "Writing",
            Description = "Translate text, preserving tone.",
            Content = "Translate the following into {{target_language}}. Preserve the " +
                      "tone and any formatting.\n\n{{text}}"
        }
    };

    private static string BuildFileName(PromptTemplate template)
    {
        var safe = new string(template.Name
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == ' ' ? c : '-')
            .ToArray())
            .Trim()
            .Replace(' ', '-')
            .ToLowerInvariant();

        if (safe.Length == 0)
            safe = "template";

        // The id keeps two templates with the same name from overwriting each other.
        return $"{safe}-{template.Id.ToString()[..8]}.md";
    }

    private string Serialize(PromptTemplate template)
    {
        var frontmatter = new
        {
            id = template.Id,
            name = template.Name,
            description = template.Description,
            category = template.Category,
            system_prompt = template.SystemPrompt,
            invocable = template.Invocable,
            created = template.CreatedAt.ToString("o"),
            updated = template.UpdatedAt.ToString("o")
        };

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(_yamlSerializer.Serialize(frontmatter).TrimEnd());
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(template.Content);

        return sb.ToString();
    }

    private PromptTemplate Parse(string content, string filePath)
    {
        var parts = content.Split(new[] { "---" }, StringSplitOptions.None);
        if (parts.Length < 3)
            throw new FormatException("Template is missing its frontmatter");

        var frontmatter = _yamlDeserializer
            .Deserialize<Dictionary<string, object>>(parts[1].Trim())
            ?? new Dictionary<string, object>();

        // Anything after the closing --- is the body; rejoin in case the prompt
        // itself contains a --- line.
        var body = string.Join("---", parts.Skip(2)).TrimStart('\r', '\n');

        return new PromptTemplate
        {
            // Values may be absent or blank in a hand-edited file, so every field
            // falls back rather than throwing.
            Id = TryGuid(frontmatter, "id") ?? Guid.NewGuid(),
            Name = Text(frontmatter, "name", Path.GetFileNameWithoutExtension(filePath)),
            Description = Text(frontmatter, "description", string.Empty),
            Category = Text(frontmatter, "category", "General"),
            SystemPrompt = Text(frontmatter, "system_prompt", string.Empty),
            // Absent from every template written before skills existed, and
            // absent means not invocable - the cautious reading.
            Invocable = bool.TryParse(Text(frontmatter, "invocable", string.Empty), out var invocable) && invocable,
            CreatedAt = TryDate(frontmatter, "created") ?? DateTime.UtcNow,
            UpdatedAt = TryDate(frontmatter, "updated") ?? DateTime.UtcNow,
            Content = body.TrimEnd(),
            FilePath = filePath
        };
    }

    private static string Text(Dictionary<string, object> map, string key, string fallback) =>
        map.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } s ? s : fallback;

    private static Guid? TryGuid(Dictionary<string, object> map, string key) =>
        Guid.TryParse(Text(map, key, string.Empty), out var id) ? id : null;

    private static DateTime? TryDate(Dictionary<string, object> map, string key) =>
        DateTime.TryParse(Text(map, key, string.Empty), out var date) ? date : null;
}
