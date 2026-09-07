using Microsoft.Extensions.Logging.Abstractions;
using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// Running a program is what closes the write-build-run-fix loop. These cover
/// the reporting and the guards; the end-to-end proof (build a real C++ program
/// and run it) lives in <see cref="BuildToolsIntegrationTests"/>.
/// </summary>
public class RunProgramToolTests
{
    private static ProcessResult Result(
        int exitCode = 0,
        string output = "",
        string standardError = "",
        bool timedOut = false,
        string? startupError = null) =>
        new()
        {
            ExitCode = exitCode,
            Output = output,
            StandardError = standardError,
            TimedOut = timedOut,
            StartupError = startupError
        };

    [Fact]
    public void A_program_that_worked_reports_success_and_what_it_printed()
    {
        var report = RunProgramTool.Describe(Result(output: "well done\n"), "well_done.exe");

        Assert.Contains("finished successfully", report);
        Assert.Contains("well done", report);
    }

    [Fact]
    public void A_program_that_printed_nothing_says_so()
    {
        // Otherwise "succeeded" with an empty body reads as output being lost.
        Assert.Contains("printed nothing", RunProgramTool.Describe(Result(), "quiet.exe"));
    }

    [Fact]
    public void A_nonzero_exit_code_is_reported()
    {
        Assert.Contains("exited with code 2", RunProgramTool.Describe(Result(exitCode: 2), "app.exe"));
    }

    [Theory]
    [InlineData(unchecked((int)0xC0000005), "access violation")]
    [InlineData(unchecked((int)0xC0000374), "heap corruption")]
    [InlineData(unchecked((int)0xC00000FD), "stack overflow")]
    [InlineData(unchecked((int)0xC000013A), "Ctrl+C")]
    public void A_windows_crash_code_is_translated_into_something_actionable(int code, string expected)
    {
        // Raw, these arrive as large negative numbers that mean nothing, and
        // they are exactly what a C++ debugging session produces.
        var described = RunProgramTool.DescribeExitCode(code);

        Assert.Contains(expected, described);
        Assert.Contains("0xC0000", described);
    }

    [Fact]
    public void An_ordinary_exit_code_is_left_as_a_plain_number()
    {
        Assert.Equal("3", RunProgramTool.DescribeExitCode(3));
    }

    [Fact]
    public void A_timeout_suggests_the_thing_that_usually_causes_it()
    {
        // A program blocked on input is the common case, and 'stdin' is the fix.
        var report = RunProgramTool.Describe(Result(timedOut: true), "waits.exe");

        Assert.Contains("time ran out", report);
        Assert.Contains("stdin", report);
    }

    [Fact]
    public void A_program_that_could_not_start_says_why()
    {
        var report = RunProgramTool.Describe(
            Result(startupError: "could not run 'x': not found"), "x.exe");

        Assert.Contains("could not start", report);
        Assert.Contains("not found", report);
    }

    [Fact]
    public void Complaints_on_the_error_stream_are_flagged_even_when_the_program_succeeded()
    {
        // Exit code 0 with a warning on stderr is easy to misread as clean.
        var report = RunProgramTool.Describe(
            Result(output: "working\nbad input ignored\n", standardError: "bad input ignored\n"),
            "app.exe");

        Assert.Contains("finished successfully", report);
        Assert.Contains("error stream", report);
    }

    [Fact]
    public void Short_output_is_returned_whole()
    {
        var output = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));

        Assert.Equal(output, RunProgramTool.Trim(output));
    }

    [Fact]
    public void Long_output_keeps_the_start_and_the_end()
    {
        // A usage message is at the top and a crash is at the bottom; both
        // matter, so the middle is what goes.
        var output = string.Join("\n", Enumerable.Range(1, 1000).Select(i => $"line {i}"));

        var trimmed = RunProgramTool.Trim(output, headLines: 10, tailLines: 10);

        Assert.Contains("line 1", trimmed);
        Assert.Contains("line 1000", trimmed);
        Assert.Contains("980 lines omitted", trimmed);
        Assert.DoesNotContain("line 500", trimmed);
    }

    [Fact]
    public void Trailing_blank_lines_do_not_count_as_output()
    {
        Assert.Equal("only line", RunProgramTool.Trim("only line\n\n\n"));
    }
}
