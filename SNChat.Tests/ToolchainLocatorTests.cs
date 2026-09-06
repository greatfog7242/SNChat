using SNChat.BuildTools;

namespace SNChat.Tests;

/// <summary>
/// Visual Studio bundles cmake, ctest and MSBuild and puts none of them on the
/// system PATH. A developer command prompt adds them, so they look installed
/// when checked from one - but the app is launched from Explorer and inherits
/// the plain registry PATH, where a bare "cmake" fails with "the system cannot
/// find the file specified" on a machine that plainly has CMake.
/// </summary>
public class ToolchainLocatorTests
{
    [Fact]
    public void An_explicit_full_path_is_used_as_given()
    {
        var existing = typeof(ToolchainLocatorTests).Assembly.Location;

        Assert.Equal(existing, ToolchainLocator.Resolve(existing, "cmake", "irrelevant"));
    }

    [Fact]
    public void An_explicit_path_that_does_not_exist_resolves_to_nothing()
    {
        // Reported rather than launched, so the failure names the setting that
        // is wrong instead of surfacing as a bare Win32 error.
        var missing = Path.Combine(Path.GetTempPath(), "no-such-tool-" + Guid.NewGuid().ToString("N"), "cmake.exe");

        Assert.Null(ToolchainLocator.Resolve(missing, "cmake", "irrelevant"));
    }

    [Fact]
    public void A_bare_name_in_the_setting_is_searched_for_rather_than_run_as_a_path()
    {
        // Someone typing "cmake" into the box means "find it", not "run the file
        // called cmake in the current directory".
        var resolved = ToolchainLocator.Resolve("cmake", "ignored", @"Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe");

        if (resolved != null)
            Assert.EndsWith("cmake.exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Something_certain_to_be_absent_resolves_to_nothing()
    {
        Assert.Null(ToolchainLocator.Resolve(null, "snchat-definitely-not-a-real-tool", "nowhere"));
    }

    [Fact]
    public void The_not_found_message_explains_the_visual_studio_trap()
    {
        // "cmake was not found" on a machine with Visual Studio installed is a
        // baffling thing to be told, so the message says why that happens.
        var message = ToolchainLocator.NotFoundMessage("cmake", "cmake executable");

        Assert.Contains("PATH", message);
        Assert.Contains("Visual Studio", message);
        Assert.Contains("Settings", message);
    }

    /// <summary>
    /// The regression test for the reported bug. Skipped where no Visual Studio
    /// is installed, since there would be nothing to find.
    /// </summary>
    [Fact]
    public void Cmake_is_found_on_a_machine_with_visual_studio_even_when_not_on_the_path()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var visualStudio = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft Visual Studio");

        var visualStudioX86 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio");

        if (!Directory.Exists(visualStudio) && !Directory.Exists(visualStudioX86))
            return;

        var cmake = ToolchainLocator.FindCMake(null);

        Assert.NotNull(cmake);
        Assert.True(File.Exists(cmake), $"resolved to {cmake}, which does not exist");
        Assert.EndsWith("cmake.exe", cmake!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ctest_is_taken_from_the_same_folder_as_a_configured_cmake()
    {
        // A ctest from a different installation would be a version mismatch
        // against the build folder cmake generated.
        var directory = Path.Combine(Path.GetTempPath(), "snchat-ctest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var cmake = Path.Combine(directory, "cmake.exe");
            var ctest = Path.Combine(directory, OperatingSystem.IsWindows() ? "ctest.exe" : "ctest");
            File.WriteAllText(cmake, string.Empty);
            File.WriteAllText(ctest, string.Empty);

            Assert.Equal(ctest, ToolchainLocator.FindCTest(cmake));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void A_configured_cmake_with_no_ctest_beside_it_resolves_to_nothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "snchat-noctest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);

        try
        {
            var cmake = Path.Combine(directory, "cmake.exe");
            File.WriteAllText(cmake, string.Empty);

            Assert.Null(ToolchainLocator.FindCTest(cmake));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
