using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SNChat.Core.Services;

/// <summary>
/// Loads and saves subagent definitions as markdown with YAML frontmatter,
/// matching how projects, templates and conversations are stored.
///
/// Synchronous, unlike its neighbours, and deliberately. These are a handful of
/// files of about a kilobyte each, read when the app starts and when a subagent
/// is invoked. Making them async would buy nothing measurable and costs
/// something real: startup reads them from a DI factory on the UI thread, and
/// blocking on a Task whose continuation wants that same thread is a deadlock.
/// </summary>
public class AgentDefinitionService
{
    private readonly string _directory;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ILogger<AgentDefinitionService> _logger;

    /// <summary>
    /// <paramref name="directory"/> exists so tests can point this at a
    /// temporary folder rather than the user's own profile.
    /// </summary>
    public AgentDefinitionService(ILogger<AgentDefinitionService> logger, string? directory = null)
    {
        _logger = logger;

        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SNChat", "agents");

        Directory.CreateDirectory(_directory);

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public string Directory_ => _directory;

    /// <summary>Whether any agent exists, without parsing them, for the startup gate.</summary>
    public bool HasAny()
    {
        try
        {
            return System.IO.Directory.Exists(_directory)
                && System.IO.Directory.EnumerateFiles(_directory, "*.md").Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every agent on disk. One that fails to parse is skipped and logged, so a
    /// bad hand-edit costs its own agent and no others.
    /// </summary>
    public List<AgentDefinition> LoadAll()
    {
        var agents = new List<AgentDefinition>();

        if (!System.IO.Directory.Exists(_directory))
            return agents;

        foreach (var file in System.IO.Directory.GetFiles(_directory, "*.md"))
        {
            try
            {
                agents.Add(Parse(File.ReadAllText(file), file));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping unreadable agent {File}", file);
            }
        }

        return agents
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public void Save(AgentDefinition agent)
    {
        agent.UpdatedAt = DateTime.UtcNow;
        agent.FilePath ??= Path.Combine(_directory, BuildFileName(agent));

        File.WriteAllText(agent.FilePath, Serialize(agent));
        _logger.LogInformation("Saved agent {Name}", agent.Name);
    }

    /// <summary>
    /// Writes a small starter set on first run, so the feature is usable without
    /// having to author an agent first. Both defaults are read-only on purpose:
    /// delegating work that only looks and reports is the safe way to meet the
    /// idea for the first time.
    ///
    /// Seeds once ever, not once per empty folder. The difference matters: it is
    /// how deleting the agents turns the feature off and keeps it off, rather
    /// than having them silently reappear at the next launch.
    /// </summary>
    public void SeedDefaultsOnFirstRun()
    {
        var marker = Path.Combine(_directory, ".seeded");

        if (File.Exists(marker))
            return;

        try
        {
            // Written first. If seeding half-fails, the next launch should not
            // try again and produce duplicates of whatever did get written.
            File.WriteAllText(marker,
                "The default subagents were written here once. Delete this file to have " +
                "them written again.\n");

            if (HasAny())
                return;

            foreach (var agent in BuildDefaults())
                Save(agent);

            _logger.LogInformation("Seeded default subagents");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not seed the default subagents");
        }
    }

    private static List<AgentDefinition> BuildDefaults() => new()
    {
        new AgentDefinition
        {
            Name = "explorer",
            Description =
                "Reads around a codebase and reports what it found. Use it when you need " +
                "to know where something is or how it works, rather than reading many " +
                "files into this conversation yourself.",
            AllowedTools = new List<string>
            {
                "read_file", "read_text_file", "read_multiple_files",
                "list_directory", "directory_tree", "search_files",
                "get_file_info", "list_projects"
            },
            SystemPrompt =
                "You investigate and report. Read what you need, then answer in a few " +
                "sentences: what you found, and where. Quote only the lines that matter. " +
                "Say plainly when you could not find something rather than guessing at it. " +
                "You cannot change anything, so do not propose edits as though you had made them."
        },
        new AgentDefinition
        {
            Name = "checker",
            Description =
                "Builds and tests a project and reports what failed. Use it to find out " +
                "whether something works without pulling a whole build log into this " +
                "conversation.",
            AllowedTools = new List<string> { "list_projects", "build_project", "run_tests", "run_program" },
            SystemPrompt =
                "You check whether things work. Build it, run its tests, run it if that " +
                "makes sense, and report the outcome: what passed, what failed, and the " +
                "exact errors for the failures. Do not attempt to fix anything - report so " +
                "that someone else can."
        }
    };

    private static string BuildFileName(AgentDefinition agent)
    {
        var safe = new string(agent.Name
            .Select(c => char.IsLetterOrDigit(c) || c == '-' ? c : '-')
            .ToArray())
            .Trim('-')
            .ToLowerInvariant();

        if (safe.Length == 0)
            safe = "agent";

        return $"{safe}-{agent.Id.ToString()[..8]}.md";
    }

    private string Serialize(AgentDefinition agent)
    {
        var frontmatter = new
        {
            id = agent.Id,
            name = agent.Name,
            description = agent.Description,
            allowed_tools = agent.AllowedTools,
            model = agent.Model,
            max_tool_iterations = agent.MaxToolIterations,
            created = agent.CreatedAt.ToString("o"),
            updated = agent.UpdatedAt.ToString("o")
        };

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(_yamlSerializer.Serialize(frontmatter).TrimEnd());
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(agent.SystemPrompt);

        return sb.ToString();
    }

    private AgentDefinition Parse(string content, string filePath)
    {
        if (!MarkdownDocument.TrySplit(content, out var frontmatterYaml, out var body))
            throw new FormatException("Agent file is missing its frontmatter");

        var frontmatter = _yamlDeserializer
            .Deserialize<Dictionary<string, object>>(frontmatterYaml)
            ?? new Dictionary<string, object>();

        return new AgentDefinition
        {
            Id = TryGuid(frontmatter, "id") ?? Guid.NewGuid(),
            Name = Text(frontmatter, "name", Path.GetFileNameWithoutExtension(filePath)),
            Description = Text(frontmatter, "description", string.Empty),
            AllowedTools = TryList(frontmatter, "allowed_tools"),
            Model = Text(frontmatter, "model", string.Empty),
            MaxToolIterations = TryInt(frontmatter, "max_tool_iterations") ?? 10,
            CreatedAt = TryDate(frontmatter, "created") ?? DateTime.UtcNow,
            UpdatedAt = TryDate(frontmatter, "updated") ?? DateTime.UtcNow,
            SystemPrompt = body.Trim(),
            FilePath = filePath
        };
    }

    private static string Text(Dictionary<string, object> map, string key, string fallback) =>
        map.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } s ? s : fallback;

    private static Guid? TryGuid(Dictionary<string, object> map, string key) =>
        Guid.TryParse(Text(map, key, string.Empty), out var id) ? id : null;

    private static int? TryInt(Dictionary<string, object> map, string key) =>
        int.TryParse(Text(map, key, string.Empty), out var value) ? value : null;

    private static DateTime? TryDate(Dictionary<string, object> map, string key) =>
        DateTime.TryParse(Text(map, key, string.Empty), out var value) ? value.ToUniversalTime() : null;

    /// <summary>
    /// A YAML list, or empty. Written to tolerate a hand-edited file that puts a
    /// single name where a list belongs, since that is a natural mistake.
    /// </summary>
    private static List<string> TryList(Dictionary<string, object> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value == null)
            return new List<string>();

        if (value is IEnumerable<object> items)
        {
            return items
                .Select(item => item?.ToString()?.Trim() ?? string.Empty)
                .Where(item => item.Length > 0)
                .ToList();
        }

        var single = value.ToString()?.Trim();

        return string.IsNullOrEmpty(single) ? new List<string>() : new List<string> { single };
    }
}
