using Microsoft.Extensions.Logging.Abstractions;
using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// Creating the repository the checkpoint needs, rather than refusing and
/// sending the user away to do it by hand.
///
/// Driven against real git, because what "git init" leaves behind - whether
/// rev-parse HEAD then succeeds, whether a commit is possible in an empty
/// folder - is a property of git rather than of anything guessable.
/// </summary>
public class GitCheckpointInitTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "snchat-init-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly GitCheckpointService _checkpoints;
    private readonly ProcessRunner _runner = new(NullLogger<ProcessRunner>.Instance);

    private static readonly string? Git = ToolchainLocator.FindOnPath("git");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public GitCheckpointInitTests()
    {
        Directory.CreateDirectory(_folder);

        _checkpoints = new GitCheckpointService(_runner, NullLogger<GitCheckpointService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_folder, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);

            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// An identity local to this repository, so the test does not depend on the
    /// machine having a global one - and does not quietly pass because it has.
    /// </summary>
    private async Task SetIdentity()
    {
        await _runner.RunAsync(Git!, new[] { "config", "user.email", "t@example.com" }, _folder, Timeout);
        await _runner.RunAsync(Git!, new[] { "config", "user.name", "Test" }, _folder, Timeout);
    }

    [Fact]
    public async Task A_folder_that_is_not_a_repository_is_reported_as_that_and_nothing_else()
    {
        // The caller only offers to create one for this specific reason, so the
        // reason has to be distinguishable.
        if (Git == null)
            return;

        var result = await _checkpoints.PrepareAsync(_folder, required: true);

        Assert.False(result.CanProceed);
        Assert.Equal(CheckpointProblem.NotARepository, result.Problem);
    }

    [Fact]
    public async Task Creating_one_leaves_a_checkpoint_that_can_be_returned_to()
    {
        if (Git == null)
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "main.py"), "print('hello')");

        // git init first so the identity can be set locally; InitialiseAsync is
        // safe to run over an already-initialised folder.
        await _runner.RunAsync(Git, new[] { "init" }, _folder, Timeout);
        await SetIdentity();

        var created = await _checkpoints.InitialiseAsync(_folder);

        Assert.True(created.CanProceed, created.Message);
        Assert.False(string.IsNullOrWhiteSpace(created.Commit));

        // And the run may now start, which is the entire point.
        var after = await _checkpoints.PrepareAsync(_folder, required: true);

        Assert.True(after.CanProceed, after.Message);
        Assert.Equal(CheckpointProblem.None, after.Problem);
    }

    [Fact]
    public async Task What_was_already_there_is_committed_rather_than_left_loose()
    {
        // A file left uncommitted would make the folder dirty, and the very next
        // check would refuse the run it just enabled.
        if (Git == null)
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "notes.txt"), "keep me");

        await _runner.RunAsync(Git, new[] { "init" }, _folder, Timeout);
        await SetIdentity();

        await _checkpoints.InitialiseAsync(_folder);

        var tracked = await _runner.RunAsync(Git, new[] { "ls-files" }, _folder, Timeout);

        Assert.Contains("notes.txt", tracked.Output);

        var status = await _runner.RunAsync(Git, new[] { "status", "--porcelain" }, _folder, Timeout);

        Assert.True(string.IsNullOrWhiteSpace(status.Output), $"Folder still dirty: {status.Output}");
    }

    [Fact]
    public async Task An_empty_folder_still_gets_something_to_go_back_to()
    {
        // A brand-new project has nothing to commit, and git refuses an empty
        // commit unless told otherwise - leaving no HEAD, and so no checkpoint.
        if (Git == null)
            return;

        await _runner.RunAsync(Git, new[] { "init" }, _folder, Timeout);
        await SetIdentity();

        var created = await _checkpoints.InitialiseAsync(_folder);

        Assert.True(created.CanProceed, created.Message);

        var head = await _runner.RunAsync(Git, new[] { "rev-parse", "HEAD" }, _folder, Timeout);

        Assert.True(head.Succeeded);
    }

    [Fact]
    public async Task What_gitignore_excludes_is_still_excluded()
    {
        // The dialog warns that build output gets committed; an ignore file the
        // user has already written must still be honoured.
        if (Git == null)
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, ".gitignore"), "*.log\n");
        await File.WriteAllTextAsync(Path.Combine(_folder, "noise.log"), "chatter");
        await File.WriteAllTextAsync(Path.Combine(_folder, "real.py"), "x = 1");

        await _runner.RunAsync(Git, new[] { "init" }, _folder, Timeout);
        await SetIdentity();

        await _checkpoints.InitialiseAsync(_folder);

        var tracked = await _runner.RunAsync(Git, new[] { "ls-files" }, _folder, Timeout);

        Assert.Contains("real.py", tracked.Output);
        Assert.DoesNotContain("noise.log", tracked.Output);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_refused_rather_than_created()
    {
        var result = await _checkpoints.InitialiseAsync(Path.Combine(_folder, "nope", "deeper"));

        Assert.False(result.CanProceed);
        Assert.Equal(CheckpointProblem.NoFolder, result.Problem);
    }

    [Fact]
    public async Task An_existing_repository_with_uncommitted_work_is_not_offered_this()
    {
        // Committing somebody's work in progress under a message they did not
        // write is not a thing to do on their behalf. That refusal stands.
        if (Git == null)
            return;

        await _runner.RunAsync(Git, new[] { "init" }, _folder, Timeout);
        await SetIdentity();
        await File.WriteAllTextAsync(Path.Combine(_folder, "first.txt"), "one");
        await _runner.RunAsync(Git, new[] { "add", "." }, _folder, Timeout);
        await _runner.RunAsync(Git, new[] { "commit", "-m", "first" }, _folder, Timeout);

        await File.WriteAllTextAsync(Path.Combine(_folder, "second.txt"), "two");

        var result = await _checkpoints.PrepareAsync(_folder, required: true);

        Assert.False(result.CanProceed);
        Assert.Equal(CheckpointProblem.Uncommitted, result.Problem);
    }
}
