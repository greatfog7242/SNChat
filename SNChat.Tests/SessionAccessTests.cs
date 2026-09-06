using SNChat.Core.Services;

namespace SNChat.Tests;

/// <summary>
/// Granting the assistant a folder for the length of a session.
///
/// The parsing half is driven by the message a real filesystem MCP server
/// actually produces, captured by running one. A refusal that is not recognised
/// means the user is never asked, and the only symptom is the assistant saying
/// it cannot read the file - which looks exactly like it not having the tool.
/// </summary>
public class SessionAccessTests
{
    // Verbatim from @modelcontextprotocol/server-filesystem 0.2.0, asked to read
    // a file outside its allowed directories. Note the '#', which this
    // repository's own path contains.
    private const string RealRefusal =
        @"Access denied - path outside allowed directories: D:\Projects\c#\SNChat\HANDOFF.md not in C:\ai-playground";

    [Fact]
    public void The_message_a_real_server_sends_is_recognised()
    {
        Assert.True(McpAccessDenial.TryParse(RealRefusal, out var path, out var allowed));

        Assert.Equal(@"D:\Projects\c#\SNChat\HANDOFF.md", path);
        Assert.Equal(@"C:\ai-playground", allowed);
    }

    [Fact]
    public void Several_allowed_roots_are_reported_whole()
    {
        Assert.True(McpAccessDenial.TryParse(
            @"Access denied - path outside allowed directories: D:\x\y.txt not in C:\one, C:\two",
            out _, out var allowed));

        Assert.Equal(@"C:\one, C:\two", allowed);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ENOENT: no such file or directory, open 'C:\\ai-playground\\gone.txt'")]
    [InlineData("Tool error: the search server timed out")]
    [InlineData("Error: EACCES: permission denied")]
    public void An_ordinary_failure_is_not_mistaken_for_a_request_for_permission(string? message)
    {
        // Offering to widen a folder because a file was simply missing would
        // put a security question in front of the user for no reason at all.
        Assert.False(McpAccessDenial.TryParse(message, out _, out _));
    }

    // --- What gets granted -------------------------------------------------

    [Fact]
    public void A_file_is_granted_by_its_folder()
    {
        Assert.Equal(@"D:\work\notes", SessionAccessGrants.FolderToGrant(@"D:\work\notes\today.md"));
    }

    [Fact]
    public void A_folder_is_granted_as_itself()
    {
        var folder = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);

        Assert.Equal(folder, SessionAccessGrants.FolderToGrant(folder));
    }

    [Fact]
    public void A_drive_root_is_never_offered()
    {
        // Granting C:\ hands over the whole machine in answer to a question
        // about one file, and the dialog would not make that obvious.
        Assert.False(SessionAccessGrants.MayBeGranted(@"C:\", out var reason));
        Assert.Contains("whole drive", reason);
    }

    [Fact]
    public void A_system_folder_is_never_offered()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.False(SessionAccessGrants.MayBeGranted(windows, out var reason));
        Assert.Contains("system folder", reason);

        Assert.False(SessionAccessGrants.MayBeGranted(
            Path.Combine(windows, "System32", "drivers"), out _));
    }

    [Fact]
    public void An_ordinary_folder_is_offered()
    {
        Assert.True(SessionAccessGrants.MayBeGranted(@"D:\Projects\c#\SNChat", out _));
    }

    // --- Grants and refusals ----------------------------------------------

    [Fact]
    public void A_granted_folder_covers_what_is_inside_it()
    {
        var grants = new SessionAccessGrants();
        grants.Grant(@"D:\work");

        Assert.True(grants.IsGranted(@"D:\work"));
        Assert.True(grants.IsGranted(@"D:\work\deep\inside\file.txt"));
    }

    [Fact]
    public void A_folder_that_merely_starts_the_same_is_not_covered()
    {
        // "C:\workshop" is not inside "C:\work", and comparing as strings says
        // it is. The build guard was written around this and it is no less
        // wrong here.
        var grants = new SessionAccessGrants();
        grants.Grant(@"C:\work");

        Assert.False(grants.IsGranted(@"C:\workshop\secrets.txt"));
    }

    [Fact]
    public void Climbing_out_of_a_granted_folder_is_not_covered()
    {
        var grants = new SessionAccessGrants();
        grants.Grant(@"C:\work");

        Assert.False(grants.IsGranted(@"C:\work\..\private\keys.txt"));
    }

    [Fact]
    public void A_refusal_is_remembered_for_the_session()
    {
        // Without this the same dialog appears on every retry, and an assistant
        // working on its own retries a great deal. A question asked forty times
        // is answered carelessly the fortieth.
        var grants = new SessionAccessGrants();
        grants.Refuse(@"D:\private");

        Assert.True(grants.IsRefused(@"D:\private\accounts.xlsx"));
        Assert.False(grants.IsGranted(@"D:\private\accounts.xlsx"));
    }

    [Fact]
    public void Changing_your_mind_and_granting_clears_the_refusal()
    {
        var grants = new SessionAccessGrants();
        grants.Refuse(@"D:\private");
        grants.Grant(@"D:\private");

        Assert.True(grants.IsGranted(@"D:\private\file.txt"));
        Assert.False(grants.IsRefused(@"D:\private\file.txt"));
    }

    [Fact]
    public void Granting_twice_is_not_two_grants()
    {
        var grants = new SessionAccessGrants();
        grants.Grant(@"D:\work");
        grants.Grant(@"D:\work\");
        grants.Grant(@"d:\WORK");

        Assert.Single(grants.Granted);
    }

    [Fact]
    public void Nothing_is_granted_to_begin_with()
    {
        // The default has to be no. A permission system that starts open is not
        // one.
        Assert.Empty(new SessionAccessGrants().Granted);
        Assert.False(new SessionAccessGrants().IsGranted(@"D:\anything"));
    }

    [Fact]
    public void Clearing_takes_everything_back()
    {
        var grants = new SessionAccessGrants();
        grants.Grant(@"D:\work");
        grants.Refuse(@"D:\private");
        grants.Clear();

        Assert.Empty(grants.Granted);
        Assert.False(grants.IsGranted(@"D:\work\file.txt"));
        Assert.False(grants.IsRefused(@"D:\private\file.txt"));
    }

    [Fact]
    public async Task With_nobody_to_ask_the_answer_is_no()
    {
        Assert.False(await new DenyAllAccessPrompt()
            .RequestAccessAsync(@"D:\work", @"D:\work\f.txt", "read_text_file"));
    }
}
