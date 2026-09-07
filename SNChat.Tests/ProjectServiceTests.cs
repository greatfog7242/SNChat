using Microsoft.Extensions.Logging.Abstractions;
using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Projects decide two things that matter: which folder the assistant may build
/// and run in, and how much it may do unattended. Both are read back from a
/// hand-editable file, so what a damaged or partial file falls back to is a
/// safety question rather than a tidiness one.
/// </summary>
public class ProjectServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "snchat-projects-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly ProjectService _service;

    public ProjectServiceTests()
    {
        _service = new ProjectService(NullLogger<ProjectService>.Instance, _directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp folder is not worth failing a test over.
        }
    }

    private async Task<Project> RoundTrip(Project project)
    {
        await _service.SaveAsync(project);
        return (await _service.LoadAsync(project.FilePath!))!;
    }

    private async Task WriteRaw(string name, string content) =>
        await File.WriteAllTextAsync(Path.Combine(_directory, name), content);

    [Fact]
    public async Task Everything_about_a_project_survives_being_written_and_read_back()
    {
        var original = new Project
        {
            Name = "Well Done",
            RootPath = @"C:\ai-playground\well_done",
            Autonomy = AutonomyMode.FullAuto,
            MaxLoopIterations = 40,
            MaxLoopMinutes = 15,
            RequireGitCheckpoint = false,
            Notes = "Scratch C++ project."
        };

        var read = await RoundTrip(original);

        Assert.Equal(original.Id, read.Id);
        Assert.Equal("Well Done", read.Name);
        Assert.Equal(@"C:\ai-playground\well_done", read.RootPath);
        Assert.Equal(AutonomyMode.FullAuto, read.Autonomy);
        Assert.Equal(40, read.MaxLoopIterations);
        Assert.Equal(15, read.MaxLoopMinutes);
        Assert.False(read.RequireGitCheckpoint);
        Assert.Equal("Scratch C++ project.", read.Notes);
    }

    [Fact]
    public void A_new_project_is_cautious_before_anyone_has_chosen()
    {
        // The defaults are what an unattended run inherits if nobody thought
        // about it, so they have to be the careful ones.
        var project = new Project();

        Assert.Equal(AutonomyMode.StepApprove, project.Autonomy);
        Assert.True(project.RequireGitCheckpoint);
    }

    [Fact]
    public async Task An_unreadable_autonomy_setting_falls_back_to_asking_first()
    {
        // A typo in a hand-edited file must never be the thing that switches
        // unattended running on.
        await WriteRaw("typo.md", """
            ---
            id: 8a9c1f2e-0000-0000-0000-000000000001
            name: Typo
            root_path: C:\work
            autonomy: FullAtuo
            ---
            """);

        var project = Assert.Single(await _service.LoadAllAsync());

        Assert.Equal(AutonomyMode.StepApprove, project.Autonomy);
    }

    [Fact]
    public async Task Autonomy_is_read_whatever_the_casing()
    {
        await WriteRaw("casing.md", """
            ---
            name: Casing
            root_path: C:\work
            autonomy: fullauto
            ---
            """);

        Assert.Equal(AutonomyMode.FullAuto, Assert.Single(await _service.LoadAllAsync()).Autonomy);
    }

    [Fact]
    public async Task A_missing_git_checkpoint_setting_keeps_the_way_back()
    {
        // The safe reading of a damaged file is the one that still takes a
        // checkpoint, not the one that skips it.
        await WriteRaw("partial.md", """
            ---
            name: Partial
            root_path: C:\work
            ---
            """);

        Assert.True(Assert.Single(await _service.LoadAllAsync()).RequireGitCheckpoint);
    }

    [Fact]
    public async Task One_unreadable_file_does_not_hide_the_others()
    {
        await _service.SaveAsync(new Project { Name = "Good", RootPath = @"C:\work" });
        await WriteRaw("broken.md", "this file has no frontmatter at all");

        var projects = await _service.LoadAllAsync();

        Assert.Equal("Good", Assert.Single(projects).Name);
    }

    [Fact]
    public async Task Notes_containing_a_horizontal_rule_survive()
    {
        // The frontmatter delimiter and a markdown rule are the same three
        // characters, so a naive split loses everything after the first one.
        var read = await RoundTrip(new Project
        {
            Name = "Ruled",
            RootPath = @"C:\work",
            Notes = "before\n\n---\n\nafter"
        });

        Assert.Contains("before", read.Notes);
        Assert.Contains("after", read.Notes);
    }

    [Fact]
    public async Task Two_projects_with_the_same_name_do_not_overwrite_each_other()
    {
        await _service.SaveAsync(new Project { Name = "Same", RootPath = @"C:\a" });
        await _service.SaveAsync(new Project { Name = "Same", RootPath = @"C:\b" });

        Assert.Equal(2, (await _service.LoadAllAsync()).Count);
    }

    [Fact]
    public async Task Removing_a_project_leaves_the_folder_it_pointed_at_alone()
    {
        // The project file is ours; the directory is the user's work.
        var folder = Path.Combine(_directory, "actual-work");
        Directory.CreateDirectory(folder);

        var project = new Project { Name = "Temp", RootPath = folder };
        await _service.SaveAsync(project);
        await _service.DeleteAsync(project);

        Assert.Empty(await _service.LoadAllAsync());
        Assert.True(Directory.Exists(folder), "deleting a project must not delete its folder");
    }

    [Fact]
    public void A_project_whose_folder_has_gone_reports_itself_as_broken()
    {
        var project = new Project { RootPath = Path.Combine(_directory, "not-there") };

        Assert.False(project.RootExists);
    }
}
