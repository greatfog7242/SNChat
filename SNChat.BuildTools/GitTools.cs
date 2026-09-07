using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.BuildTools;

/// <summary>
/// The few git operations the assistant may perform, and no others.
///
/// git can do a great deal that has no place in an unattended run. Pushing is
/// outward-facing and irreversible from here; reset, checkout and clean destroy
/// work, including the very checkpoint that makes a run undoable. None of them
/// are offered. What is offered is seeing what changed and committing it, which
/// is what turns "works on its own until it needs to save" into something that
/// actually finishes.
///
/// Committing stays safe because the checkpoint taken before a run points at the
/// commit it started from: "git reset --hard &lt;checkpoint&gt;" discards anything
/// committed since, so the way back survives the assistant using this.
/// </summary>
internal static class GitScope
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The folder these tools work in: the active project's root, and nothing
    /// else. Deliberately narrower than the other tools, which accept a path -
    /// there is no reason for the assistant to be committing anywhere but the
    /// project it is working in, and a path argument is one more thing that
    /// could point somewhere unintended.
    /// </summary>
    internal static string? Folder(ProjectContext projects)
    {
        var root = projects.Current?.RootPath;

        return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : root;
    }

    internal static string NoProject =>
        "There is no project selected, so there is no repository to work in. " +
        "Pick a project in the toolbar first.";

    internal static string NoGit =>
        "git could not be found on this machine.";
}

/// <summary>Reports what has changed, so a commit can describe it truthfully.</summary>
public class GitStatusTool : ITool
{
    private readonly ProjectContext _projects;
    private readonly ProcessRunner _runner;

    public string Name => "git_status";

    public string Description =>
        "List the files changed in the current project since the last commit. " +
        "Use it before git_commit so the message describes what actually changed.";

    public ToolParameterSchema Parameters => new();

    public GitStatusTool(ProjectContext projects, ProcessRunner runner)
    {
        _projects = projects;
        _runner = runner;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var folder = GitScope.Folder(_projects);

        if (folder == null)
            return GitScope.NoProject;

        var git = ToolchainLocator.FindOnPath("git");

        if (git == null)
            return GitScope.NoGit;

        var result = await _runner.RunAsync(
            git, new[] { "status", "--porcelain" }, folder, GitScope.Timeout, cancellationToken);

        if (!result.Succeeded)
            return $"Could not read the repository: {BuildOutputParser.Tail(result.Output, 10)}";

        var changes = result.Output
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Trim().Length > 0)
            .ToList();

        if (changes.Count == 0)
            return "Nothing has changed since the last commit.";

        var report = new StringBuilder();
        report.AppendLine($"{changes.Count} change(s):");

        foreach (var change in changes.Take(50))
            report.AppendLine("  " + change);

        if (changes.Count > 50)
            report.AppendLine($"  ... and {changes.Count - 50} more");

        return report.ToString().TrimEnd();
    }
}

/// <summary>
/// Commits everything currently changed in the project, with a message the
/// assistant writes.
/// </summary>
public class GitCommitTool : ITool
{
    private readonly SettingsService _settingsService;
    private readonly ProjectContext _projects;
    private readonly ProcessRunner _runner;
    private readonly ILogger<GitCommitTool> _logger;

    /// <summary>
    /// Added to every commit this makes, so that looking back through a
    /// project's history it is possible to tell which commits were the
    /// assistant's and which were the user's own.
    /// </summary>
    public const string Trailer = "Committed by the SNChat assistant.";

    public string Name => "git_commit";

    public string Description =>
        "Commit the current project's changes with a message. Use it after you " +
        "have made a change work, so the next step has something to go back to. " +
        "It cannot push, reset or discard anything - only commit.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["message"] = new()
            {
                Type = "string",
                Description = "What changed and why, in a sentence. Describe the actual " +
                              "change, not the task you were given."
            }
        },
        Required = new List<string> { "message" }
    };

    public GitCommitTool(
        SettingsService settingsService,
        ProjectContext projects,
        ProcessRunner runner,
        ILogger<GitCommitTool> logger)
    {
        _settingsService = settingsService;
        _projects = projects;
        _runner = runner;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!_settingsService.GetCachedSettings().BuildTools.AllowCommit)
            return "Committing is turned off. Enable it under Settings - Build tools.";

        var folder = GitScope.Folder(_projects);

        if (folder == null)
            return GitScope.NoProject;

        var git = ToolchainLocator.FindOnPath("git");

        if (git == null)
            return GitScope.NoGit;

        var message = arguments.TryGetValue("message", out var raw)
            ? raw?.ToString()?.Trim() ?? string.Empty
            : string.Empty;

        if (string.IsNullOrWhiteSpace(message))
            return "Error: a commit needs a message saying what changed.";

        // Staged first so that files the assistant created are included; a new
        // source file left uncommitted would be a surprising thing to discover
        // later. What .gitignore excludes stays excluded.
        var staged = await _runner.RunAsync(
            git, new[] { "add", "-A" }, folder, GitScope.Timeout, cancellationToken);

        if (!staged.Succeeded)
            return $"Could not stage the changes: {BuildOutputParser.Tail(staged.Output, 10)}";

        var result = await _runner.RunAsync(
            git,
            new[] { "commit", "-m", message, "-m", Trailer },
            folder, GitScope.Timeout, cancellationToken);

        // git reports "nothing to commit" as a failure, which it is not - it
        // simply means the work was already saved.
        if (!result.Succeeded)
        {
            if (result.Output.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return "Nothing had changed, so nothing was committed.";

            return $"The commit failed: {BuildOutputParser.Tail(result.Output, 10)}";
        }

        var describe = await _runner.RunAsync(
            git, new[] { "log", "--oneline", "-1" }, folder, GitScope.Timeout, cancellationToken);

        var committed = describe.Output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? message;

        _logger.LogInformation("Assistant committed in {Folder}: {Commit}", folder, committed);

        return $"Committed: {committed}";
    }
}
