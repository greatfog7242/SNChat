using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SNChat.Core.Services;

/// <summary>
/// Loads and saves projects as markdown files with YAML frontmatter, matching
/// how conversations and templates are stored so they stay hand-editable.
/// </summary>
public class ProjectService
{
    private readonly string _projectsDirectory;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;
    private readonly ILogger<ProjectService> _logger;

    /// <summary>
    /// <paramref name="projectsDirectory"/> exists so tests can point this at a
    /// temporary folder. Left null everywhere else, which uses the real location
    /// under AppData - a service that can only ever write to the user's own
    /// profile cannot be tested without polluting it.
    /// </summary>
    public ProjectService(ILogger<ProjectService> logger, string? projectsDirectory = null)
    {
        _logger = logger;

        _projectsDirectory = projectsDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SNChat", "projects");

        Directory.CreateDirectory(_projectsDirectory);

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public string ProjectsDirectory => _projectsDirectory;

    /// <summary>
    /// Whether any project exists at all, without parsing them. Used at startup
    /// to decide whether the build and run tools are worth registering, which
    /// has to happen before anything async has run.
    /// </summary>
    public bool HasAnyProjects()
    {
        try
        {
            return Directory.Exists(_projectsDirectory)
                && Directory.EnumerateFiles(_projectsDirectory, "*.md").Any();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Every project on disk. A file that fails to parse is skipped and logged
    /// rather than aborting the load, so one bad hand-edit does not hide the rest.
    /// </summary>
    public async Task<List<Project>> LoadAllAsync()
    {
        var projects = new List<Project>();

        if (!Directory.Exists(_projectsDirectory))
            return projects;

        foreach (var file in Directory.GetFiles(_projectsDirectory, "*.md"))
        {
            try
            {
                var project = await LoadAsync(file);
                if (project != null)
                    projects.Add(project);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read the project file {File}", file);
            }
        }

        return projects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<Project?> LoadAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        return Parse(await File.ReadAllTextAsync(filePath), filePath);
    }

    public async Task SaveAsync(Project project)
    {
        project.UpdatedAt = DateTime.UtcNow;
        project.FilePath ??= Path.Combine(_projectsDirectory, BuildFileName(project));

        await File.WriteAllTextAsync(project.FilePath, Serialize(project));
        _logger.LogInformation("Saved project {Name} at {Root}", project.Name, project.RootPath);
    }

    public Task DeleteAsync(Project project)
    {
        // Deletes only the project file. The folder it points at is the user's
        // own work and is never touched.
        if (!string.IsNullOrEmpty(project.FilePath) && File.Exists(project.FilePath))
        {
            File.Delete(project.FilePath);
            _logger.LogInformation("Removed project {Name}", project.Name);
        }

        return Task.CompletedTask;
    }

    private static string BuildFileName(Project project)
    {
        var safe = new string(project.Name
            .Select(c => char.IsLetterOrDigit(c) || c == '-' || c == ' ' ? c : '-')
            .ToArray())
            .Trim()
            .Replace(' ', '-')
            .ToLowerInvariant();

        if (safe.Length == 0)
            safe = "project";

        // The id keeps two projects with the same name from overwriting each other.
        return $"{safe}-{project.Id.ToString()[..8]}.md";
    }

    private string Serialize(Project project)
    {
        var frontmatter = new
        {
            id = project.Id,
            name = project.Name,
            root_path = project.RootPath,
            autonomy = project.Autonomy.ToString(),
            max_loop_iterations = project.MaxLoopIterations,
            max_loop_minutes = project.MaxLoopMinutes,
            require_git_checkpoint = project.RequireGitCheckpoint,
            created = project.CreatedAt.ToString("o"),
            updated = project.UpdatedAt.ToString("o")
        };

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine(_yamlSerializer.Serialize(frontmatter).TrimEnd());
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(project.Notes);

        return sb.ToString();
    }

    private Project Parse(string content, string filePath)
    {
        // Split on delimiter lines rather than the substring; notes containing
        // three hyphens would otherwise tear the frontmatter in half.
        if (!MarkdownDocument.TrySplit(content, out var frontmatterYaml, out var body))
            throw new FormatException("Project file is missing its frontmatter");

        var frontmatter = _yamlDeserializer
            .Deserialize<Dictionary<string, object>>(frontmatterYaml)
            ?? new Dictionary<string, object>();

        return new Project
        {
            // Every field falls back rather than throwing: these files are meant
            // to be hand-editable, so any of them may be absent or blank.
            Id = TryGuid(frontmatter, "id") ?? Guid.NewGuid(),
            Name = Text(frontmatter, "name", Path.GetFileNameWithoutExtension(filePath)),
            RootPath = Text(frontmatter, "root_path", string.Empty),
            Autonomy = TryAutonomy(frontmatter, "autonomy"),
            MaxLoopIterations = TryInt(frontmatter, "max_loop_iterations") ?? 25,
            MaxLoopMinutes = TryInt(frontmatter, "max_loop_minutes") ?? 30,
            // Defaults to true when absent or unreadable: the safe reading of a
            // damaged file is the one that keeps the way back.
            RequireGitCheckpoint = TryBool(frontmatter, "require_git_checkpoint") ?? true,
            CreatedAt = TryDate(frontmatter, "created") ?? DateTime.UtcNow,
            UpdatedAt = TryDate(frontmatter, "updated") ?? DateTime.UtcNow,
            Notes = body.TrimEnd(),
            FilePath = filePath
        };
    }

    private static string Text(Dictionary<string, object> map, string key, string fallback) =>
        map.TryGetValue(key, out var value) && value?.ToString() is { Length: > 0 } s ? s : fallback;

    private static Guid? TryGuid(Dictionary<string, object> map, string key) =>
        Guid.TryParse(Text(map, key, string.Empty), out var id) ? id : null;

    private static int? TryInt(Dictionary<string, object> map, string key) =>
        int.TryParse(Text(map, key, string.Empty), out var value) ? value : null;

    private static bool? TryBool(Dictionary<string, object> map, string key) =>
        bool.TryParse(Text(map, key, string.Empty), out var value) ? value : null;

    private static DateTime? TryDate(Dictionary<string, object> map, string key) =>
        DateTime.TryParse(Text(map, key, string.Empty), out var value) ? value.ToUniversalTime() : null;

    /// <summary>
    /// An unrecognised autonomy setting falls back to the cautious one rather
    /// than the permissive one - a typo in a hand-edited file must not be what
    /// turns unattended running on.
    /// </summary>
    private static AutonomyMode TryAutonomy(Dictionary<string, object> map, string key) =>
        Enum.TryParse<AutonomyMode>(Text(map, key, string.Empty), ignoreCase: true, out var mode)
            ? mode
            : AutonomyMode.StepApprove;
}
