using System.Text;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.BuildTools;

/// <summary>
/// Tells the model what it is allowed to build, so it can name a real project
/// instead of guessing a path and being refused.
/// </summary>
public class ListProjectsTool : ITool
{
    private readonly SettingsService _settingsService;
    private readonly ProjectContext _projects;

    public string Name => "list_projects";

    public string Description =>
        "List the solutions and projects the assistant is allowed to build or " +
        "test on this machine, with their paths. Call this first when the user " +
        "asks to build, compile or test something and no path is known yet.";

    public ToolParameterSchema Parameters => new();

    public ListProjectsTool(SettingsService settingsService, ProjectContext projects)
    {
        _settingsService = settingsService;
        _projects = projects;
    }

    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var guard = new WorkspaceGuard(
            _projects.EffectiveRoots(_settingsService.GetCachedSettings().BuildTools));

        if (!guard.HasRoots)
            return Task.FromResult(guard.DenialMessage(string.Empty));

        var report = new StringBuilder();
        var total = 0;

        foreach (var root in guard.Roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targets = ProjectLocator.Find(root);
            report.AppendLine($"{root}:");

            if (targets.Count == 0)
            {
                report.AppendLine("  (nothing buildable found)");
                continue;
            }

            foreach (var target in targets)
            {
                var kind = target.Kind switch
                {
                    ProjectKind.Gradle => "gradle",
                    ProjectKind.CMake => "cmake/c++",
                    _ => "dotnet"
                };

                report.AppendLine($"  [{kind}] {target.Path}");
                total++;
            }
        }

        return Task.FromResult(total == 0
            ? report.ToString().TrimEnd()
            : $"{total} buildable project(s):\n{report.ToString().TrimEnd()}");
    }
}
