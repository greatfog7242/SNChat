using Microsoft.Extensions.Logging;

namespace SNChat.BuildTools;

/// <summary>What was found, and whether an unattended run may start.</summary>
public sealed record CheckpointResult(bool CanProceed, string Message)
{
    /// <summary>The commit the folder was at, when there is one to go back to.</summary>
    public string? Commit { get; init; }
}

/// <summary>
/// Takes a way back before the assistant works unattended.
///
/// Unattended work here writes files and runs programs, and the way back that
/// already exists on a developer's machine is git. Rather than copying the
/// folder - slow, disk-hungry and awkward to restore from - this records the
/// commit the work started at, and refuses to start when the folder is not a
/// repository or has changes that are not committed. Refusing is the point: an
/// automatic run over uncommitted work has nothing to undo to.
/// </summary>
public class GitCheckpointService
{
    private readonly ProcessRunner _runner;
    private readonly ILogger<GitCheckpointService> _logger;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public GitCheckpointService(ProcessRunner runner, ILogger<GitCheckpointService> logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public async Task<CheckpointResult> PrepareAsync(
        string folder,
        bool required,
        CancellationToken cancellationToken = default)
    {
        if (!required)
            return new CheckpointResult(true, "No checkpoint was asked for.");

        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return new CheckpointResult(false, "The project folder does not exist.");

        var git = ToolchainLocator.FindOnPath("git");

        if (git == null)
        {
            return new CheckpointResult(false,
                "git could not be found, so there would be no way back from an " +
                "unattended run. Install git, or turn the checkpoint off for this " +
                "project in Settings.");
        }

        var head = await RunAsync(git, folder, cancellationToken, "rev-parse", "HEAD");

        if (!head.Succeeded)
        {
            return new CheckpointResult(false,
                $"'{folder}' is not a git repository, so there would be no way back " +
                "from an unattended run. Put it under git, or turn the checkpoint off " +
                "for this project in Settings.");
        }

        var commit = head.Output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;

        // Porcelain output is empty exactly when nothing is changed or untracked.
        var status = await RunAsync(git, folder, cancellationToken, "status", "--porcelain");

        if (!status.Succeeded)
            return new CheckpointResult(false, $"Could not read the repository's state: {status.Output.Trim()}");

        var dirty = status.Output
            .Split('\n')
            .Count(line => line.Trim().Length > 0);

        if (dirty > 0)
        {
            return new CheckpointResult(false,
                $"'{folder}' has {dirty} uncommitted change(s). Commit or stash them first, " +
                "so there is something to go back to if the run goes wrong.")
            {
                Commit = commit
            };
        }

        _logger.LogInformation("Checkpoint for {Folder} at {Commit}", folder, Short(commit));

        return new CheckpointResult(true,
            $"Checkpoint taken at {Short(commit)}. To undo everything this run does: " +
            $"git -C \"{folder}\" reset --hard {Short(commit)}")
        {
            Commit = commit
        };
    }

    private Task<ProcessResult> RunAsync(
        string git, string folder, CancellationToken cancellationToken, params string[] arguments) =>
        _runner.RunAsync(git, arguments, folder, Timeout, cancellationToken);

    /// <summary>Short form, which is what anyone would type.</summary>
    private static string Short(string commit) =>
        commit.Length >= 8 ? commit[..8] : commit;
}
