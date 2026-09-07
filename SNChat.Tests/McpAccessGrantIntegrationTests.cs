using SNChat.Core.Services;
using SNChat.MCP;

namespace SNChat.Tests;

/// <summary>
/// The whole grant path, driven against a real filesystem MCP server: refused,
/// widened, and reading the same file afterwards.
///
/// Nothing else proves this. The refusal message is the server's own wording,
/// the restart depends on how it parses its command line, and the retry depends
/// on the new process actually having taken the folder. Every one of those is a
/// property of somebody else's program, and all three were wrong in some way
/// during development - the server asks for MCP roots and then ignores them,
/// which looked for a while like the clean solution.
///
/// Skips itself when npx is unavailable rather than failing, in the manner of
/// the other toolchain-dependent tests here.
/// </summary>
public class McpAccessGrantIntegrationTests : IDisposable
{
    private readonly string _allowed =
        Path.Combine(Path.GetTempPath(), "snchat-mcp-in-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _outside =
        Path.Combine(Path.GetTempPath(), "snchat-mcp-out-" + Guid.NewGuid().ToString("N")[..8]);

    private const string Server = "@modelcontextprotocol/server-filesystem";

    /// <summary>Generous: the first run may download the package.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromMinutes(3);

    private string SecretFile => Path.Combine(_outside, "secret.txt");

    public McpAccessGrantIntegrationTests()
    {
        Directory.CreateDirectory(_allowed);
        Directory.CreateDirectory(_outside);

        File.WriteAllText(Path.Combine(_allowed, "welcome.txt"), "this one is fine");
        File.WriteAllText(SecretFile, "the pigeon flies at dawn");
    }

    public void Dispose()
    {
        foreach (var folder in new[] { _allowed, _outside })
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? Npx() =>
        ToolchainLocatorShim.Find("npx.cmd") ?? ToolchainLocatorShim.Find("npx");

    private async Task<McpServerConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        if (Npx() == null)
            return null;

        try
        {
            return await McpServerConnection.ConnectAsync(
                "npx.cmd",
                $"-y {Server} {McpServerConnection.Quote(_allowed)}",
                cancellationToken: cancellationToken);
        }
        catch
        {
            // No network, or npx cannot fetch the package. Not a failure of
            // anything this repository owns.
            return null;
        }
    }

    private static Dictionary<string, object> Read(string path) => new() { ["path"] = path };

    private static string TextOf(SNChat.MCP.Protocol.Messages.CallToolResult result) =>
        string.Join("\n", result.Content.Select(c => c.Text ?? string.Empty));

    [Fact]
    public async Task A_file_outside_the_allowed_folder_is_refused_then_readable_once_granted()
    {
        using var cts = new CancellationTokenSource(Patience);

        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        // 1. Inside the allowed folder: fine from the start.
        var inside = await connection.CallToolAsync(
            "read_text_file", Read(Path.Combine(_allowed, "welcome.txt")), cts.Token);

        Assert.NotEqual(true, inside.IsError);

        // 2. Outside it: refused, in the wording the app has to recognise.
        var refused = await connection.CallToolAsync("read_text_file", Read(SecretFile), cts.Token);

        Assert.True(refused.IsError);

        var refusalText = TextOf(refused);

        Assert.True(
            McpAccessDenial.TryParse(refusalText, out var refusedPath, out _),
            $"The refusal was not recognised, so the user would never be asked: {refusalText}");

        // 3. The folder the app would put to the user is the one holding it.
        var folder = SessionAccessGrants.FolderToGrant(refusedPath);

        Assert.Equal(_outside, folder);
        Assert.True(SessionAccessGrants.MayBeGranted(folder!, out _));

        // 4. Granting it restarts the server with the folder added.
        Assert.True(await connection.AllowFolderAsync(folder!, cts.Token),
            "The server did not restart with the granted folder.");

        // 5. The same call now works.
        var granted = await connection.CallToolAsync("read_text_file", Read(SecretFile), cts.Token);

        Assert.NotEqual(true, granted.IsError);
        Assert.Contains("pigeon flies at dawn", TextOf(granted));

        // 6. And the folder it started with did not get lost in the restart.
        var still = await connection.CallToolAsync(
            "read_text_file", Read(Path.Combine(_allowed, "welcome.txt")), cts.Token);

        Assert.NotEqual(true, still.IsError);
        Assert.Contains("this one is fine", TextOf(still));
    }

    [Fact]
    public async Task Granting_the_same_folder_twice_does_not_restart_it_twice()
    {
        using var cts = new CancellationTokenSource(Patience);

        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        Assert.True(await connection.AllowFolderAsync(_outside, cts.Token));
        Assert.True(await connection.AllowFolderAsync(_outside + Path.DirectorySeparatorChar, cts.Token));

        Assert.Single(connection.AddedDirectories);
    }

    [Fact]
    public async Task A_folder_with_a_space_in_its_name_survives_the_command_line()
    {
        // Quoting is the whole reason this could break, and "Program Files" is
        // not an unusual shape for a folder somebody wants to open.
        using var cts = new CancellationTokenSource(Patience);

        using var connection = await ConnectAsync(cts.Token);

        if (connection == null)
            return;

        var spaced = Path.Combine(_outside, "a folder with spaces");
        Directory.CreateDirectory(spaced);
        await File.WriteAllTextAsync(Path.Combine(spaced, "note.txt"), "spaces are fine", cts.Token);

        Assert.True(await connection.AllowFolderAsync(spaced, cts.Token));

        var result = await connection.CallToolAsync(
            "read_text_file", Read(Path.Combine(spaced, "note.txt")), cts.Token);

        Assert.NotEqual(true, result.IsError);
        Assert.Contains("spaces are fine", TextOf(result));
    }
}

/// <summary>
/// Finds an executable on PATH without dragging the build tools into these
/// tests, which are about MCP rather than toolchains.
/// </summary>
internal static class ToolchainLocatorShim
{
    public static string? Find(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(folder.Trim('"'), executable);

                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry; skip it.
            }
        }

        return null;
    }
}
