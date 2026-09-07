using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// The guard is the whole security boundary for the build tools: a build runs
/// the project's own scripts, and this decides which projects those can be. The
/// model is fed web results and files from MCP servers, so the paths reaching it
/// are not always ones the user chose.
/// </summary>
public class WorkspaceGuardTests
{
    /// <summary>
    /// Rooted so the tests exercise real path resolution rather than resolution
    /// against whatever directory the test run happens to start in.
    /// </summary>
    private static string Root(params string[] parts) =>
        Path.Combine(OperatingSystem.IsWindows() ? @"C:\" : "/", Path.Combine(parts));

    private static WorkspaceGuard Guard(params string[] roots) => new(roots);

    [Fact]
    public void Nothing_is_allowed_until_a_folder_has_been_named()
    {
        // The default state. An empty list must mean "off", never "anywhere".
        var guard = Guard();

        Assert.False(guard.HasRoots);
        Assert.Null(guard.Resolve(Root("work", "app")));
    }

    [Fact]
    public void A_path_inside_an_allowed_folder_is_allowed()
    {
        var guard = Guard(Root("work"));

        Assert.NotNull(guard.Resolve(Root("work", "app", "App.csproj")));
    }

    [Fact]
    public void The_allowed_folder_itself_is_allowed()
    {
        var guard = Guard(Root("work"));

        Assert.NotNull(guard.Resolve(Root("work")));
    }

    [Fact]
    public void A_path_outside_every_allowed_folder_is_refused()
    {
        var guard = Guard(Root("work"));

        Assert.Null(guard.Resolve(Root("Windows", "System32")));
    }

    [Fact]
    public void Climbing_out_with_dot_dot_is_refused()
    {
        // The attack this exists to stop: a path that reads as though it is
        // inside the root but resolves elsewhere.
        var guard = Guard(Root("work"));

        Assert.Null(guard.Resolve(Path.Combine(Root("work"), "..", "Windows", "System32")));
    }

    [Fact]
    public void Climbing_out_and_back_in_is_still_inside()
    {
        var guard = Guard(Root("work"));

        Assert.NotNull(guard.Resolve(Path.Combine(Root("work"), "app", "..", "other")));
    }

    [Fact]
    public void A_sibling_whose_name_merely_starts_with_the_root_is_refused()
    {
        // "C:\workshop" must not pass a root of "C:\work". Comparing the strings
        // as prefixes alone would let it through.
        var guard = Guard(Root("work"));

        Assert.Null(guard.Resolve(Root("workshop", "app")));
    }

    [Fact]
    public void A_trailing_separator_on_the_root_makes_no_difference()
    {
        var guard = Guard(Root("work") + Path.DirectorySeparatorChar);

        Assert.NotNull(guard.Resolve(Root("work", "app")));
    }

    [Fact]
    public void Any_of_several_roots_will_do()
    {
        var guard = Guard(Root("work"), Root("src"));

        Assert.NotNull(guard.Resolve(Root("src", "thing")));
        Assert.NotNull(guard.Resolve(Root("work", "thing")));
        Assert.Null(guard.Resolve(Root("elsewhere", "thing")));
    }

    [Fact]
    public void Blank_entries_do_not_switch_the_tools_on()
    {
        // A trailing newline in the settings box used to be able to produce one
        // of these. An empty root matches nothing, but it must not make HasRoots
        // true either, or the tools would be offered while unusable.
        var guard = Guard("", "   ");

        Assert.False(guard.HasRoots);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_path_is_refused(string? path)
    {
        Assert.Null(Guard(Root("work")).Resolve(path));
    }

    [Fact]
    public void An_unusable_path_is_refused_rather_than_throwing()
    {
        // Reaches the tool straight from the model, so it can be anything at all.
        var guard = Guard(Root("work"));

        Assert.Null(guard.Resolve("\0not a path"));
    }

    [Fact]
    public void The_refusal_names_the_folders_that_would_work()
    {
        // So the model corrects itself instead of trying variations.
        var message = Guard(Root("work")).DenialMessage(Root("elsewhere"));

        Assert.Contains(Root("work"), message);
    }

    [Fact]
    public void The_refusal_says_so_when_nothing_is_configured_at_all()
    {
        var message = Guard().DenialMessage(Root("work"));

        Assert.Contains("Settings", message);
    }

    [Fact]
    public void Windows_paths_match_regardless_of_case()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var guard = Guard(@"C:\Work");

        Assert.NotNull(guard.Resolve(@"c:\work\app"));
    }
}
