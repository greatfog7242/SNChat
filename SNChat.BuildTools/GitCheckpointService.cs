using Microsoft.Extensions.Logging;

namespace SNChat.BuildTools;

/// <summary>
/// Why an unattended run cannot start, when it cannot.
///
/// A reason rather than only a message, because one of these is worth offering
/// to fix and the others are not. A folder that is simply not a repository yet
/// is a dead end the user has to leave the app to resolve; uncommitted work is
/// a decision only they can make.
/// </summary>
public enum CheckpointProblem
{
    None,
    NoFolder,
    NoGit,
    NotARepository,
    Uncommitted,
    Unreadable
}

/// <summary>What was found, and whether an unattended run may start.</summary>
public sealed record CheckpointResult(bool CanProceed, string Message)
{
    /// <summary>The commit the folder was at, when there is one to go back to.</summary>
    public string? Commit { get; init; }

    public CheckpointProblem Problem { get; init; } = CheckpointProblem.None;
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
        {
            return new CheckpointResult(false, "The project folder does not exist.")
            {
                Problem = CheckpointProblem.NoFolder
            };
        }

        var git = ToolchainLocator.FindOnPath("git");

        if (git == null)
        {
            return new CheckpointResult(false,
                "git could not be found, so there would be no way back from an " +
                "unattended run. Install git, or turn the checkpoint off for this " +
                "project in Settings.")
            {
                Problem = CheckpointProblem.NoGit
            };
        }

        var head = await RunAsync(git, folder, cancellationToken, "rev-parse", "HEAD");

        if (!head.Succeeded)
        {
            return new CheckpointResult(false,
                $"'{folder}' is not a git repository, so there would be no way back " +
                "from an unattended run.")
            {
                Problem = CheckpointProblem.NotARepository
            };
        }

        var commit = head.Output.Trim().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;

        // Porcelain output is empty exactly when nothing is changed or untracked.
        var status = await RunAsync(git, folder, cancellationToken, "status", "--porcelain");

        if (!status.Succeeded)
        {
            return new CheckpointResult(false,
                $"Could not read the repository's state: {status.Output.Trim()}")
            {
                Problem = CheckpointProblem.Unreadable
            };
        }

        var dirty = status.Output
            .Split('\n')
            .Count(line => line.Trim().Length > 0);

        if (dirty > 0)
        {
            return new CheckpointResult(false,
                $"'{folder}' has {dirty} uncommitted change(s). Commit or stash them first, " +
                "so there is something to go back to if the run goes wrong.")
            {
                Commit = commit,
                Problem = CheckpointProblem.Uncommitted
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

    /// <summary>
    /// Makes <paramref name="folder"/> a git repository and commits whatever is
    /// already in it, so that a run can start with something to go back to.
    ///
    /// Offered because "this is not a repository" is otherwise a dead end that
    /// sends the user out of the app to run three commands, at the exact moment
    /// they were trying to start work. Creating a repository takes nothing away
    /// and adds the undo that the refusal was asking for - unlike the other
    /// reasons a checkpoint fails, which are the user's decisions to make.
    ///
    /// The initial commit is allowed to be empty, so a brand-new project folder
    /// still ends up with a HEAD to reset to.
    /// </summary>
    public async Task<CheckpointResult> InitialiseAsync(
        string folder,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            return new CheckpointResult(false, "The project folder does not exist.")
            {
                Problem = CheckpointProblem.NoFolder
            };
        }

        var git = ToolchainLocator.FindOnPath("git");

        if (git == null)
        {
            return new CheckpointResult(false, "git could not be found on this machine.")
            {
                Problem = CheckpointProblem.NoGit
            };
        }

        var init = await RunAsync(git, folder, cancellationToken, "init");

        if (!init.Succeeded)
            return new CheckpointResult(false, $"Could not create the repository: {Tail(init.Output)}");

        var staged = await RunAsync(git, folder, cancellationToken, "add", "-A");

        if (!staged.Succeeded)
            return new CheckpointResult(false, $"Could not stage the existing files: {Tail(staged.Output)}");

        // Counted before committing, purely so the user is told. A folder with a
        // build output or a node_modules in it produces a startling number here,
        // and silently committing thirty thousand files would be worse than
        // saying so.
        var listed = await RunAsync(git, folder, cancellationToken, "diff", "--cached", "--name-only");

        var fileCount = listed.Output
            .Split('\n')
            .Count(line => line.Trim().Length > 0);

        var commit = await RunAsync(
            git, folder, cancellationToken,
            "commit", "--allow-empty", "-m", "Starting point, before the assistant works here");

        if (!commit.Succeeded)
        {
            // By far the most likely cause on a machine that has never used git.
            if (commit.Output.Contains("tell me who you are", StringComparison.OrdinalIgnoreCase)
                || commit.Output.Contains("user.email", StringComparison.OrdinalIgnoreCase))
            {
                return new CheckpointResult(false,
                    "The repository was created, but git does not know who you are, so it " +
                    "could not commit. Set that once, in any terminal:\n\n" +
                    "    git config --global user.email \"you@example.com\"\n" +
                    "    git config --global user.name \"Your Name\"");
            }

            return new CheckpointResult(false, $"Could not make the first commit: {Tail(commit.Output)}");
        }

        _logger.LogInformation(
            "Created a repository in {Folder} with {Count} file(s) committed", folder, fileCount);

        return await PrepareAsync(folder, required: true, cancellationToken) with
        {
            Message = fileCount == 0
                ? "Created a repository here, with an empty first commit to go back to."
                : $"Created a repository here and committed the {fileCount} file(s) already " +
                  "in it, as the point to go back to."
        };
    }

    private static string Tail(string output)
    {
        var lines = output
            .Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.Trim().Length > 0)
            .ToList();

        return lines.Count == 0 ? "no output" : string.Join(" ", lines.TakeLast(3));
    }

    private Task<ProcessResult> RunAsync(
        string git, string folder, CancellationToken cancellationToken, params string[] arguments) =>
        _runner.RunAsync(git, arguments, folder, Timeout, cancellationToken);

    /// <summary>Short form, which is what anyone would type.</summary>
    private static string Short(string commit) =>
        commit.Length >= 8 ? commit[..8] : commit;
}
