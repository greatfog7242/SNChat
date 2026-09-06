using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// A build prints far more than the few lines that matter, and all of it would
/// otherwise be handed to the model and charged against its context window.
/// </summary>
public class BuildOutputParserTests
{
    [Fact]
    public void An_msbuild_error_is_read_with_its_position_and_code()
    {
        var diagnostics = BuildOutputParser.Parse(
            @"C:\src\Program.cs(42,17): error CS0103: The name 'foo' does not exist in the current context");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(@"C:\src\Program.cs", diagnostic.File);
        Assert.Equal(42, diagnostic.Line);
        Assert.Equal(17, diagnostic.Column);
        Assert.Equal("CS0103", diagnostic.Code);
        Assert.Contains("does not exist", diagnostic.Message);
    }

    [Fact]
    public void A_warning_is_told_apart_from_an_error()
    {
        var diagnostics = BuildOutputParser.Parse(
            @"C:\src\A.cs(1,1): warning CS0168: The variable 'x' is declared but never used");

        Assert.Equal(DiagnosticSeverity.Warning, Assert.Single(diagnostics).Severity);
    }

    [Fact]
    public void A_project_level_error_with_no_position_still_parses()
    {
        // NuGet restore failures arrive this way - no line, no column.
        var diagnostics = BuildOutputParser.Parse(
            @"C:\src\App.csproj : error NU1101: Unable to find package Foo");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("NU1101", diagnostic.Code);
        Assert.Equal(0, diagnostic.Line);
    }

    [Fact]
    public void A_javac_error_relayed_by_gradle_is_read()
    {
        var diagnostics = BuildOutputParser.Parse(
            "/home/me/app/src/main/java/Main.java:12: error: cannot find symbol");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(12, diagnostic.Line);
        Assert.Contains("cannot find symbol", diagnostic.Message);
    }

    [Fact]
    public void A_gcc_error_with_a_column_is_read()
    {
        // The column broke the javac pattern, which expected the line number to
        // be followed straight by the severity.
        var diagnostics = BuildOutputParser.Parse(
            "/src/main.cpp:5:3: error: 'foo' was not declared in this scope");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal(5, diagnostic.Line);
        Assert.Equal(3, diagnostic.Column);
    }

    [Fact]
    public void A_fatal_error_is_an_error_and_not_a_warning()
    {
        // "fatal error" only matched on the word "error" by equality before,
        // so it was filed as a warning - hiding the thing that stopped the build.
        var diagnostics = BuildOutputParser.Parse(
            "/src/main.cpp:1:10: fatal error: iostream: No such file or directory");

        Assert.Equal(DiagnosticSeverity.Error, Assert.Single(diagnostics).Severity);
    }

    [Fact]
    public void An_msvc_error_is_read_like_any_other_msbuild_one()
    {
        var diagnostics = BuildOutputParser.Parse(
            @"D:\proj\main.cpp(5,3): error C2065: 'foo': undeclared identifier");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("C2065", diagnostic.Code);
        Assert.Equal(5, diagnostic.Line);
    }

    [Fact]
    public void The_project_name_msbuild_appends_to_every_line_is_dropped()
    {
        // Repeated on every diagnostic and often longer than the message itself,
        // so it is pure cost against the context window.
        var diagnostics = BuildOutputParser.Parse(
            @"D:\proj\main.cpp(5,3): error C2065: 'foo': undeclared identifier [D:\proj\build\app.vcxproj]");

        Assert.DoesNotContain("vcxproj", Assert.Single(diagnostics).Message);
        Assert.Contains("undeclared identifier", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void A_real_msvc_error_with_a_space_in_its_path_is_read_correctly()
    {
        // Captured verbatim from cl.exe through cmake --build. The space in
        // "Xiaozhong Chen" is the trap: a path with a space in it is exactly
        // what a loose pattern mistakes for prose and discards.
        var diagnostics = BuildOutputParser.Parse(
            @"C:\Users\Xiaozhong Chen\AppData\Local\Temp\cpptest\main.cpp(2,5): error C3861: 'undeclared_thing': identifier not found [C:\Users\Xiaozhong Chen\AppData\Local\Temp\cpptest\build\well_done.vcxproj]");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("C3861", diagnostic.Code);
        Assert.Equal(2, diagnostic.Line);
        Assert.Equal(5, diagnostic.Column);
        Assert.EndsWith("main.cpp", diagnostic.File);
        Assert.Equal("'undeclared_thing': identifier not found", diagnostic.Message);
    }

    [Fact]
    public void A_cmake_error_names_the_file_and_line_it_came_from()
    {
        var diagnostics = BuildOutputParser.Parse(
            "CMake Error at CMakeLists.txt:5 (add_executable):");

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("CMakeLists.txt", diagnostic.File);
        Assert.Equal(5, diagnostic.Line);
    }

    [Fact]
    public void A_failing_gradle_task_is_reported_even_with_no_file()
    {
        var diagnostics = BuildOutputParser.Parse(
            "Execution failed for task ':app:compileDebugJavaWithJavac'.");

        Assert.Contains(diagnostics, d => d.Message.Contains(":app:compileDebugJavaWithJavac"));
    }

    [Fact]
    public void The_same_error_repeated_is_only_reported_once()
    {
        // MSBuild repeats every diagnostic in its end-of-build summary, and once
        // more per target framework. Undeduplicated, the model reads one problem
        // as three and starts hunting for the other two.
        var output = string.Join("\n",
            @"C:\src\Program.cs(42,17): error CS0103: The name 'foo' does not exist",
            @"C:\src\Program.cs(42,17): error CS0103: The name 'foo' does not exist",
            @"C:\src\Program.cs(42,17): error CS0103: The name 'foo' does not exist");

        Assert.Single(BuildOutputParser.Parse(output));
    }

    [Fact]
    public void Ordinary_build_chatter_is_not_mistaken_for_a_diagnostic()
    {
        var output = string.Join("\n",
            "Determining projects to restore...",
            "  Restored C:\\src\\App.csproj (in 165 ms).",
            "  App -> C:\\src\\bin\\Debug\\net8.0\\App.dll",
            "Build succeeded.",
            "    0 Warning(s)",
            "    0 Error(s)");

        Assert.Empty(BuildOutputParser.Parse(output));
    }

    [Fact]
    public void A_successful_build_says_so_and_reports_nothing_else()
    {
        var summary = BuildOutputParser.Summarize(
            new ProcessResult { ExitCode = 0, Output = "Build succeeded." }, "Build of App.sln");

        Assert.Contains("succeeded", summary);
        Assert.Contains("0 error(s)", summary);
    }

    [Fact]
    public void A_command_that_could_not_start_says_which_one()
    {
        // The usual cause is dotnet not being on PATH, which the user can fix -
        // but only if told.
        var summary = BuildOutputParser.Summarize(
            new ProcessResult { StartupError = "could not run 'dotnet': not found" }, "Build");

        Assert.Contains("could not run 'dotnet'", summary);
    }

    [Fact]
    public void A_build_that_overran_is_not_reported_as_a_compile_failure()
    {
        var summary = BuildOutputParser.Summarize(
            new ProcessResult { TimedOut = true, ExitCode = -1, Output = "" }, "Build");

        Assert.Contains("ran out of time", summary);
    }

    [Fact]
    public void A_long_list_of_errors_is_capped_and_says_how_many_were_left_out()
    {
        // The point of the whole parser: a broken build can produce hundreds of
        // errors, and sending them all would flood the context window.
        var output = string.Join("\n", Enumerable.Range(1, 100)
            .Select(i => $@"C:\src\File{i}.cs({i},1): error CS0103: problem {i}"));

        var summary = BuildOutputParser.Summarize(
            new ProcessResult { ExitCode = 1, Output = output }, "Build", maxErrors: 10);

        Assert.Contains("100 error(s)", summary);
        Assert.Contains("and 90 more", summary);
        Assert.DoesNotContain("problem 99", summary);
    }

    [Fact]
    public void A_failure_nothing_recognised_still_shows_the_tail_of_the_output()
    {
        // Otherwise the model is told only that the build failed, with nothing
        // whatsoever to act on.
        var summary = BuildOutputParser.Summarize(
            new ProcessResult { ExitCode = 1, Output = "something went wrong in a way we do not parse" },
            "Build");

        Assert.Contains("something went wrong", summary);
    }
}
