using SNChat.BuildTools;
using SNChat.Core.Models;
using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// The active project widens what the build and run tools may touch, so this is
/// part of the permission boundary rather than a convenience lookup.
/// </summary>
public class ProjectContextTests
{
    private static BuildToolSettings Settings(params string[] roots) =>
        new() { AllowedRoots = roots.ToList() };

    [Fact]
    public void With_no_project_the_configured_folders_are_all_there_is()
    {
        var context = new ProjectContext();

        Assert.Equal(new[] { @"C:\work" }, context.EffectiveRoots(Settings(@"C:\work")));
    }

    [Fact]
    public void The_active_projects_folder_is_allowed_without_listing_it_twice()
    {
        // Adding a project is already the deliberate act of pointing the
        // assistant at a folder; making the user then repeat the path in
        // Settings would be a step that teaches nothing and gets skipped.
        var context = new ProjectContext
        {
            Current = new Project { Name = "Well Done", RootPath = @"C:\ai-playground\well_done" }
        };

        var roots = context.EffectiveRoots(Settings(@"C:\work"));

        Assert.Contains(@"C:\ai-playground\well_done", roots);
        Assert.Contains(@"C:\work", roots);
    }

    [Fact]
    public void A_project_alone_is_enough_to_have_somewhere_to_work()
    {
        var context = new ProjectContext
        {
            Current = new Project { RootPath = @"C:\only-project" }
        };

        var guard = new WorkspaceGuard(context.EffectiveRoots(Settings()));

        Assert.True(guard.HasRoots);
        Assert.NotNull(guard.Resolve(@"C:\only-project\src\main.cpp"));
    }

    [Fact]
    public void A_project_with_no_folder_set_grants_nothing()
    {
        // A half-created project must not switch the tools on while pointing
        // nowhere - an empty root would otherwise read as "somewhere to work".
        var context = new ProjectContext { Current = new Project { Name = "Unset" } };

        Assert.False(new WorkspaceGuard(context.EffectiveRoots(Settings())).HasRoots);
    }

    [Fact]
    public void Switching_project_stops_granting_the_previous_one()
    {
        var context = new ProjectContext { Current = new Project { RootPath = @"C:\first" } };
        var settings = Settings();

        Assert.NotNull(new WorkspaceGuard(context.EffectiveRoots(settings)).Resolve(@"C:\first\a.txt"));

        context.Current = new Project { RootPath = @"C:\second" };

        var guard = new WorkspaceGuard(context.EffectiveRoots(settings));
        Assert.Null(guard.Resolve(@"C:\first\a.txt"));
        Assert.NotNull(guard.Resolve(@"C:\second\a.txt"));
    }

    [Fact]
    public void A_project_does_not_open_up_its_parent_folder()
    {
        // The guard's containment rule still applies to a project root - the
        // project widens where work may happen, it does not relax how the
        // boundary is judged.
        var context = new ProjectContext
        {
            Current = new Project { RootPath = @"C:\ai-playground\well_done" }
        };

        var guard = new WorkspaceGuard(context.EffectiveRoots(Settings()));

        Assert.Null(guard.Resolve(@"C:\ai-playground\something-else"));
        Assert.Null(guard.Resolve(@"C:\ai-playground\well_done\..\elsewhere"));
    }
}
