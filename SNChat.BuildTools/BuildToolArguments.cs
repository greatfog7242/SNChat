using SNChat.Core.Models;

namespace SNChat.BuildTools;

/// <summary>
/// The checks a build and a test run share: that the path is allowed, that
/// something buildable is actually there, and that the configuration asked for
/// is one of the two that exist.
///
/// Kept in one place because these are the guard rails, and two copies of a
/// guard rail is one copy that eventually drifts.
/// </summary>
public static class BuildToolArguments
{
    /// <summary>
    /// The target and configuration to use, or null with <paramref name="failure"/>
    /// set to something the model can act on.
    /// </summary>
    public static (BuildTarget Target, string Configuration)? Resolve(
        IReadOnlyDictionary<string, object?> arguments,
        BuildToolSettings settings,
        out string? failure)
    {
        failure = null;

        var guard = new WorkspaceGuard(settings.AllowedRoots);

        if (!arguments.TryGetValue("path", out var rawPath) || rawPath is null)
        {
            failure = "Error: no 'path' argument was provided.";
            return null;
        }

        var requested = rawPath.ToString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(requested))
        {
            failure = "Error: the 'path' argument was empty.";
            return null;
        }

        var resolved = guard.Resolve(requested);

        if (resolved == null)
        {
            failure = guard.DenialMessage(requested);
            return null;
        }

        var target = ProjectLocator.Identify(resolved);

        if (target == null)
        {
            failure = $"Nothing buildable was found at '{resolved}'. " +
                      "Expected a .sln, a project file, or a folder containing one, " +
                      "or a Gradle project. Call list_projects to see what is available.";
            return null;
        }

        return (target, Configuration(arguments));
    }

    /// <summary>
    /// Debug unless Release was asked for. Anything else is treated as Debug
    /// rather than passed along, so the value cannot become a way to smuggle
    /// extra arguments onto the command line.
    /// </summary>
    private static string Configuration(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("configuration", out var raw) || raw is null)
            return "Debug";

        return raw.ToString()?.Trim().Equals("Release", StringComparison.OrdinalIgnoreCase) == true
            ? "Release"
            : "Debug";
    }

    /// <summary>
    /// The Gradle wrapper for a project, as an explicit path so that the working
    /// directory is what decides which project is built. Falls back to a plain
    /// "gradle" on PATH when a project has no wrapper checked in.
    /// </summary>
    public static string GradleExecutable(BuildTarget target)
    {
        var wrapper = Path.Combine(target.WorkingDirectory, ProjectLocator.GradleWrapperName);

        return File.Exists(wrapper) ? wrapper : "gradle";
    }
}
