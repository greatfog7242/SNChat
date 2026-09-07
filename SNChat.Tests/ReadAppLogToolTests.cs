using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// The tool exists so the assistant can find out why one of its own tool calls
/// was refused - when edit_file was failing on every call, the reason was in the
/// log and nothing could see it.
/// </summary>
public class ReadAppLogToolTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "snchat-logs-" + Guid.NewGuid().ToString("N")[..8]);

    public ReadAppLogToolTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private ReadAppLogTool Tool() => new(_directory);

    private void WriteLog(string name, params string[] lines) =>
        File.WriteAllLines(Path.Combine(_directory, name), lines);

    private static Task<string> Run(ReadAppLogTool tool, params (string Key, object? Value)[] arguments) =>
        tool.ExecuteAsync(arguments.ToDictionary(a => a.Key, a => a.Value));

    [Fact]
    public async Task The_end_of_the_log_is_returned()
    {
        WriteLog("snchat-20260906.log", "first", "second", "third");

        var report = await Run(Tool());

        Assert.Contains("third", report);
        Assert.Contains("first", report);
    }

    [Fact]
    public async Task Only_the_requested_number_of_lines_comes_back()
    {
        WriteLog("snchat-20260906.log", Enumerable.Range(1, 200).Select(i => $"line {i}").ToArray());

        var report = await Run(Tool(), ("lines", 5));

        Assert.Contains("line 200", report);
        Assert.DoesNotContain("line 100", report);
    }

    [Fact]
    public async Task An_absurd_line_count_is_clamped_rather_than_honoured()
    {
        // Comes straight from the model, and a request for a million lines would
        // otherwise return the entire log into the context window.
        WriteLog("snchat-20260906.log", Enumerable.Range(1, 5000).Select(i => $"line {i}").ToArray());

        var report = await Run(Tool(), ("lines", 999999));

        Assert.True(report.Split('\n').Length < 600, "the line count was not clamped");
    }

    [Fact]
    public async Task Filtering_returns_only_matching_lines()
    {
        WriteLog("snchat-20260906.log",
            "Executing tool image_search",
            "Executing tool edit_file",
            "Tool edit_file returned: expected array, received string",
            "Executing tool build_project");

        var report = await Run(Tool(), ("contains", "edit_file"));

        Assert.Contains("expected array", report);
        Assert.DoesNotContain("image_search", report);
    }

    [Fact]
    public async Task Filtering_ignores_case()
    {
        WriteLog("snchat-20260906.log", "Tool EDIT_FILE returned something");

        Assert.Contains("EDIT_FILE", await Run(Tool(), ("contains", "edit_file")));
    }

    [Fact]
    public async Task The_newest_log_is_read_even_when_the_name_sorts_lower()
    {
        // Serilog rolls by date and again by size, so today's second file can
        // sort before yesterday's. Picking by name would read the wrong one.
        WriteLog("snchat-20260906.log", "yesterday-ish");
        await Task.Delay(20);
        WriteLog("snchat-20260906_001.log", "the newest entry");

        Assert.Contains("the newest entry", await Run(Tool()));
    }

    [Fact]
    public async Task A_log_open_for_writing_can_still_be_read()
    {
        // The logger holds the current file open, so an exclusive open would
        // fail on every single call.
        var path = Path.Combine(_directory, "snchat-20260906.log");
        await File.WriteAllLinesAsync(path, new[] { "while open" });

        using var held = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        Assert.Contains("while open", await Run(Tool()));
    }

    [Fact]
    public async Task No_log_at_all_is_said_plainly()
    {
        Assert.Contains("no log file", await Run(Tool()));
    }

    [Fact]
    public async Task A_filter_matching_nothing_says_so_rather_than_returning_the_whole_log()
    {
        WriteLog("snchat-20260906.log", "something", "else");

        var report = await Run(Tool(), ("contains", "absent-text"));

        Assert.Contains("absent-text", report);
        Assert.DoesNotContain("something", report);
    }
}
