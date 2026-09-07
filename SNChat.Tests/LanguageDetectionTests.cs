using SNChat.BuildTools;
using SNChat.Core.Models;

namespace SNChat.Tests;

/// <summary>
/// Which language a folder is decides which commands get run in it, so getting
/// the guess wrong is not cosmetic. Real projects routinely carry more than one
/// marker file, which is why the order is fixed and tested rather than incidental.
/// </summary>
public class LanguageDetectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "snchat-lang-" + Guid.NewGuid().ToString("N")[..8]);

    public LanguageDetectionTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Folder(string name, params string[] files)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);

        foreach (var file in files)
        {
            var full = Path.Combine(directory, file);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "{}");
        }

        return directory;
    }

    private static ProjectKind? KindOf(string directory) =>
        ProjectLocator.Identify(directory)?.Kind;

    [Theory]
    [InlineData("pom.xml", ProjectKind.Maven)]
    [InlineData("package.json", ProjectKind.Node)]
    [InlineData("Gemfile", ProjectKind.Ruby)]
    [InlineData("pyproject.toml", ProjectKind.Python)]
    [InlineData("setup.py", ProjectKind.Python)]
    [InlineData("requirements.txt", ProjectKind.Python)]
    public void A_marker_file_identifies_its_language(string marker, ProjectKind expected)
    {
        Assert.Equal(expected, KindOf(Folder(marker.Replace('.', '-'), marker)));
    }

    [Fact]
    public void A_solution_still_wins_over_a_language_marker()
    {
        // A .NET solution with a package.json for its front-end assets is
        // extremely common, and the solution is what you build.
        var directory = Folder("dotnet-with-node", "App.sln", "package.json");

        Assert.Equal(ProjectKind.DotNet, KindOf(directory));
    }

    [Fact]
    public void A_java_pom_beats_a_helper_scripts_requirements_file()
    {
        // A requirements.txt for a build helper does not make a Java project a
        // Python one.
        var directory = Folder("java-with-python-helper", "pom.xml", "requirements.txt");

        Assert.Equal(ProjectKind.Maven, KindOf(directory));
    }

    [Fact]
    public void A_python_service_with_front_end_assets_is_read_as_node()
    {
        // Documented rather than defended: package.json is checked before the
        // Python markers, so this is the wrong answer for a Python project that
        // happens to build front-end assets. list_projects reports the kind so
        // it is visible rather than silent, and the path can be given explicitly.
        var directory = Folder("python-with-assets", "pyproject.toml", "package.json");

        Assert.Equal(ProjectKind.Node, KindOf(directory));
    }

    [Fact]
    public void A_gradle_wrapper_still_wins_over_everything_that_follows()
    {
        var directory = Folder("android", "gradlew", "build.gradle.kts", "package.json");

        Assert.Equal(ProjectKind.Gradle, KindOf(directory));
    }

    [Fact]
    public void A_folder_with_nothing_recognisable_is_not_a_project()
    {
        Assert.Null(KindOf(Folder("empty", "notes.txt")));
    }

    [Fact]
    public void Rails_is_told_apart_from_a_plain_ruby_project()
    {
        // config/application.rb is the file Rails boots from, and it decides
        // whether tests run through rails or through rspec.
        var rails = ProjectLocator.Identify(
            Folder("rails-app", "Gemfile", Path.Combine("config", "application.rb")))!;
        var plain = ProjectLocator.Identify(Folder("plain-ruby", "Gemfile"))!;

        Assert.True(ProjectLocator.IsRails(rails));
        Assert.False(ProjectLocator.IsRails(plain));
    }

    [Fact]
    public void Every_language_kind_has_a_build_and_a_test_command()
    {
        // A kind that is detected but has no commands would be found by
        // list_projects and then refuse every action, which is worse than not
        // detecting it at all.
        var settings = new BuildToolSettings();

        foreach (var kind in new[]
                 { ProjectKind.Maven, ProjectKind.Node, ProjectKind.Python, ProjectKind.Ruby })
        {
            var target = new BuildTarget(kind, Path.Combine(_root, "x"), "x");

            // Either a runnable command, or a problem naming the missing tool -
            // never the "no command is known" fallback.
            Assert.DoesNotContain("No build command is known",
                Toolchains.Build(target, "Debug", settings).Problem ?? string.Empty);
            Assert.DoesNotContain("No test command is known",
                Toolchains.Test(target, "Debug", settings).Problem ?? string.Empty);
        }
    }

    [Fact]
    public void Python_tests_fall_back_to_unittest_when_pytest_is_absent()
    {
        // A machine with Python but no pytest is ordinary. Without the fallback
        // it reports "No module named pytest", which reads as a broken tool
        // rather than a missing package. unittest ships with Python.
        var command = Toolchains.Test(
            new BuildTarget(ProjectKind.Python, _root, "py"), "Debug", new BuildToolSettings());

        if (!command.CanRun)
            return;

        var expected = Toolchains.HasPytest() ? "pytest" : "unittest";

        Assert.Contains(expected, string.Join(" ", command.Arguments));
    }

    [Fact]
    public void The_two_things_run_program_cannot_do_are_stated_rather_than_failing_oddly()
    {
        // Android needs a device, and a Rails server never exits. Both are real
        // limits, so the tool should say so instead of timing out.
        Assert.Contains("adb", Toolchains.RunLimitation(ProjectKind.Gradle));
        Assert.Contains("does not exit", Toolchains.RunLimitation(ProjectKind.Ruby));
        Assert.Null(Toolchains.RunLimitation(ProjectKind.Python));
    }
}
