using Microsoft.Extensions.Logging.Abstractions;
using SNChat.BuildTools;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.Tests;

/// <summary>
/// The assistant can see what changed and commit it, and nothing else. Pushing
/// is outward-facing and irreversible from here; reset, checkout and clean
/// destroy work, including the checkpoint that makes an unattended run undoable.
///
/// Driven against real repositories, because what git does with a given argument
/// is not something worth guessing at.
/// </summary>
public class GitToolsTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "snchat-git-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ProcessRunner _runner = new(NullLogger<ProcessRunner>.Instance);
    private readonly ProjectContext _projects = new();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private static readonly string? Git = ToolchainLocator.FindOnPath("git");

    public GitToolsTests()
    {
        Directory.CreateDirectory(_folder);
        _projects.Current = new Project { Name = "scratch", RootPath = _folder };
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

    private async Task<bool> InitRepo()
    {
        if (Git == null)
            return false;

        await _runner.RunAsync(Git, new[] { "init" }, _folder, Timeout);
        await _runner.RunAsync(Git, new[] { "config", "user.email", "t@example.com" }, _folder, Timeout);
        await _runner.RunAsync(Git, new[] { "config", "user.name", "Test" }, _folder, Timeout);

        await File.WriteAllTextAsync(Path.Combine(_folder, "first.txt"), "one");

        await _runner.RunAsync(Git, new[] { "add", "." }, _folder, Timeout);
        var commit = await _runner.RunAsync(Git, new[] { "commit", "-m", "first" }, _folder, Timeout);

        return commit.Succeeded;
    }

    private GitStatusTool Status() => new(_projects, _runner);

    private GitCommitTool Commit() => new(
        new SettingsService(), _projects, _runner, NullLogger<GitCommitTool>.Instance);

    private static Task<string> Run(ITool tool, params (string Key, object? Value)[] arguments) =>
        tool.ExecuteAsync(arguments.ToDictionary(a => a.Key, a => a.Value));

    private async Task<string> Log()
    {
        var log = await _runner.RunAsync(Git!, new[] { "log", "--oneline" }, _folder, Timeout);
        return log.Output;
    }

    [Fact]
    public async Task Status_reports_what_has_changed()
    {
        if (!await InitRepo())
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "second.txt"), "two");

        var report = await Run(Status());

        Assert.Contains("second.txt", report);
    }

    [Fact]
    public async Task Status_says_plainly_when_nothing_has_changed()
    {
        if (!await InitRepo())
            return;

        Assert.Contains("Nothing has changed", await Run(Status()));
    }

    [Fact]
    public async Task Committing_saves_the_work_and_reports_the_commit()
    {
        if (!await InitRepo())
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "first.txt"), "changed");

        var report = await Run(Commit(), ("message", "Change the first file"));

        Assert.Contains("Committed:", report);
        Assert.Contains("Change the first file", await Log());
    }

    [Fact]
    public async Task A_file_the_assistant_created_is_included()
    {
        // A new source file left uncommitted would be a surprising thing to
        // find later, so staging covers untracked files too.
        if (!await InitRepo())
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "brand-new.py"), "print('hi')");

        await Run(Commit(), ("message", "Add a new file"));

        var tracked = await _runner.RunAsync(Git!, new[] { "ls-files" }, _folder, Timeout);

        Assert.Contains("brand-new.py", tracked.Output);
    }

    [Fact]
    public async Task What_gitignore_excludes_stays_excluded()
    {
        // The .pyc that got committed during a real run was a missing ignore
        // file, not the tool overreaching - staging must still respect one.
        if (!await InitRepo())
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, ".gitignore"), "*.pyc\n");
        await File.WriteAllTextAsync(Path.Combine(_folder, "junk.pyc"), "bytecode");

        await Run(Commit(), ("message", "Add the ignore file"));

        var tracked = await _runner.RunAsync(Git!, new[] { "ls-files" }, _folder, Timeout);

        Assert.DoesNotContain("junk.pyc", tracked.Output);
    }

    [Fact]
    public async Task Committing_when_nothing_changed_is_reported_rather_than_failing()
    {
        // git treats "nothing to commit" as an error exit, which it is not.
        if (!await InitRepo())
            return;

        Assert.Contains("Nothing had changed", await Run(Commit(), ("message", "No-op")));
    }

    [Fact]
    public async Task A_commit_without_a_message_is_refused()
    {
        if (!await InitRepo())
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "first.txt"), "changed");

        Assert.Contains("needs a message", await Run(Commit(), ("message", "   ")));
    }

    [Fact]
    public async Task The_assistants_commits_are_identifiable_afterwards()
    {
        // So a project's history shows which commits were the assistant's.
        if (!await InitRepo())
            return;

        await File.WriteAllTextAsync(Path.Combine(_folder, "first.txt"), "changed");
        await Run(Commit(), ("message", "A change"));

        var body = await _runner.RunAsync(
            Git!, new[] { "log", "-1", "--format=%B" }, _folder, Timeout);

        Assert.Contains(GitCommitTool.Trailer, body.Output);
    }

    [Fact]
    public async Task With_no_project_selected_there_is_no_repository_to_touch()
    {
        // Narrower than the other tools on purpose: these take no path at all,
        // so there is nothing that could point somewhere unintended.
        _projects.Current = null;

        Assert.Contains("no project selected", await Run(Status()));
        Assert.Contains("no project selected", await Run(Commit(), ("message", "x")));
    }

    [Fact]
    public void Nothing_destructive_or_outward_facing_is_offered()
    {
        // The whole point of the narrow scope. If a push, reset, checkout or
        // clean ever appears in these tools, this fails.
        var source = File.ReadAllText(
            Path.Combine(FindRepoRoot(), "SNChat.BuildTools", "GitTools.cs"));

        // Only the argument lists matter; the prose explains why they are absent.
        var arguments = source
            .Split('\n')
            .Where(line => line.Contains("new[] {") && line.Contains('"'))
            .ToList();

        Assert.NotEmpty(arguments);

        foreach (var forbidden in new[] { "push", "reset", "checkout", "clean", "rebase", "remote" })
        {
            Assert.DoesNotContain(arguments,
                line => line.Contains($"\"{forbidden}\""));
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "SNChat.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
