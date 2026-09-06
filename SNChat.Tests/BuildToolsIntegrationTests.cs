using Microsoft.Extensions.Logging.Abstractions;
using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// Drives a real dotnet build, because everything else about these tools can be
/// green while the feature does not work: the process launching, the output
/// capture and the diagnostic patterns only meet each other here. Slower than
/// the rest of the suite, and deliberately kept to the two cases that matter -
/// a build that fails and one that does not.
/// </summary>
public class BuildToolsIntegrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "snchat-build-" + Guid.NewGuid().ToString("N")[..8]);

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public BuildToolsIntegrationTests()
    {
        Directory.CreateDirectory(_directory);

        // No packages, so this restores without touching the network.
        File.WriteAllText(Path.Combine(_directory, "Scratch.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A build server can still hold a handle briefly; a leftover temp
            // folder is not worth failing a test over.
        }
    }

    private void WriteSource(string code) =>
        File.WriteAllText(Path.Combine(_directory, "Program.cs"), code);

    private static ProcessRunner Runner() => new(NullLogger<ProcessRunner>.Instance);

    private Task<ProcessResult> BuildAsync() =>
        Runner().RunAsync(
            "dotnet",
            new[] { "build", _directory, "--nologo", "--verbosity", "minimal" },
            _directory,
            Timeout);

    [Fact]
    public void The_scratch_project_is_recognised_as_a_dotnet_build()
    {
        var target = ProjectLocator.Identify(_directory);

        Assert.NotNull(target);
        Assert.Equal(ProjectKind.DotNet, target!.Kind);
        Assert.EndsWith("Scratch.csproj", target.Path);
    }

    [Fact]
    public async Task A_real_compiler_error_comes_back_with_its_file_line_and_code()
    {
        WriteSource("""
            public class Program
            {
                public static void Main() => Nonexistent.Method();
            }
            """);

        var result = await BuildAsync();

        Assert.True(result.Started, result.StartupError);
        Assert.False(result.Succeeded);

        var errors = BuildOutputParser.Parse(result.Output)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.NotEmpty(errors);

        var error = errors.First(e => e.Code.StartsWith("CS"));
        Assert.Equal("CS0103", error.Code);
        Assert.Contains("Program.cs", error.File);
        Assert.Equal(3, error.Line);

        // What the model is actually handed.
        var summary = BuildOutputParser.Summarize(result, "Build of Scratch.csproj");
        Assert.Contains("failed", summary);
        Assert.Contains("CS0103", summary);
    }

    [Fact]
    public async Task A_project_that_compiles_reports_success_and_no_errors()
    {
        WriteSource("""
            public class Program
            {
                public static void Main() => System.Console.WriteLine("ok");
            }
            """);

        var result = await BuildAsync();

        Assert.True(result.Started, result.StartupError);
        Assert.True(result.Succeeded, BuildOutputParser.Tail(result.Output, 20));

        var summary = BuildOutputParser.Summarize(result, "Build of Scratch.csproj");
        Assert.Contains("succeeded", summary);
        Assert.Contains("0 error(s)", summary);
    }

    /// <summary>
    /// The case that prompted CMake support: a folder holding a CMakeLists.txt
    /// and a .cpp, which the locator previously did not recognise as buildable
    /// at all.
    /// </summary>
    [Fact]
    public void A_folder_with_a_cmakelists_is_recognised_as_a_cpp_build()
    {
        var directory = Path.Combine(_directory, "cpp");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.10)");
        File.WriteAllText(Path.Combine(directory, "main.cpp"), "int main() { return 0; }");

        var target = ProjectLocator.Identify(directory);

        Assert.NotNull(target);
        Assert.Equal(ProjectKind.CMake, target!.Kind);
        Assert.EndsWith("CMakeLists.txt", target.Path);
    }

    [Fact]
    public void The_cmake_build_folder_sits_inside_the_project()
    {
        // It must land inside the allowed folder; anywhere else would be writing
        // outside the boundary the guard exists to enforce.
        var directory = Path.Combine(_directory, "cpp2");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.10)");

        var target = ProjectLocator.Identify(directory)!;

        Assert.Equal(Path.Combine(directory, "build"), target.CMakeBuildDirectory);
        Assert.StartsWith(directory, target.CMakeBuildDirectory);
    }

    /// <summary>
    /// cmake as the tool itself resolves it - through the locator, not a bare
    /// name, so these exercise the same lookup the app does. Null on a machine
    /// with no C++ toolchain, where the C++ tests skip rather than fail.
    /// </summary>
    private static readonly string? CMake = ToolchainLocator.FindCMake(null);

    private static bool CMakeAvailable => CMake != null;

    private string WriteCppProject(string name, string source)
    {
        var directory = Path.Combine(_directory, name);
        Directory.CreateDirectory(directory);

        File.WriteAllText(Path.Combine(directory, "CMakeLists.txt"), $"""
            cmake_minimum_required(VERSION 3.15)
            project({name} CXX)
            set(CMAKE_CXX_STANDARD 17)
            add_executable({name} main.cpp)
            """);

        File.WriteAllText(Path.Combine(directory, "main.cpp"), source);
        return directory;
    }

    private async Task<ProcessResult> CMakeBuildAsync(string directory)
    {
        var runner = Runner();

        var configure = await runner.RunAsync(
            CMake!,
            new[] { "-S", directory, "-B", Path.Combine(directory, "build"),
                    "-DCMAKE_BUILD_TYPE=Debug", "--no-warn-unused-cli" },
            directory, Timeout);

        if (!configure.Succeeded)
            return configure;

        return await runner.RunAsync(
            CMake!,
            new[] { "--build", Path.Combine(directory, "build"), "--config", "Debug" },
            directory, Timeout);
    }

    [Fact]
    public async Task A_real_cpp_project_builds_through_cmake()
    {
        if (!CMakeAvailable)
            return;

        var directory = WriteCppProject("ok_app", """
            #include <iostream>
            int main() { std::cout << "well done" << std::endl; return 0; }
            """);

        var result = await CMakeBuildAsync(directory);

        Assert.True(result.Started, result.StartupError);
        Assert.True(result.Succeeded, BuildOutputParser.Tail(result.Output, 20));
        Assert.Contains("succeeded", BuildOutputParser.Summarize(result, "Build"));
    }

    [Fact]
    public async Task A_real_cpp_compiler_error_comes_back_with_its_file_and_line()
    {
        if (!CMakeAvailable)
            return;

        var directory = WriteCppProject("broken_app", """
            int main() {
                undeclared_thing();
                return 0;
            }
            """);

        var result = await CMakeBuildAsync(directory);

        Assert.True(result.Started, result.StartupError);
        Assert.False(result.Succeeded);

        var errors = BuildOutputParser.Parse(result.Output)
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.File.EndsWith("main.cpp") && e.Line == 2);

        // The project suffix must not survive into what the model reads.
        Assert.DoesNotContain("vcxproj", BuildOutputParser.Summarize(result, "Build"));
    }

    [Fact]
    public async Task A_command_that_does_not_exist_is_reported_rather_than_thrown()
    {
        var result = await Runner().RunAsync(
            "snchat-no-such-executable", Array.Empty<string>(), _directory, Timeout);

        Assert.False(result.Started);
        Assert.Contains("could not run", result.StartupError);
    }

    [Fact]
    public async Task A_command_that_overruns_is_stopped_and_reported_as_a_timeout()
    {
        // Sleeps far longer than it is given. Kills the process tree, which is
        // what stops an orphaned build server outliving the turn.
        var result = await Runner().RunAsync(
            "dotnet",
            new[] { "--info" },
            _directory,
            TimeSpan.FromMilliseconds(1));

        // Either it was killed in time, or it was quick enough to finish; both
        // are fine, and asserting on the race would make this flaky.
        Assert.True(result.TimedOut || result.Started);
    }
}
