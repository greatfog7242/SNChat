using System.Diagnostics;

namespace SNChat.BuildTools;

/// <summary>
/// Finds the build tools that ship inside Visual Studio.
///
/// This is the point of the whole feature and the thing that is easy to get
/// wrong: Visual Studio bundles cmake, ninja and MSBuild, and puts none of them
/// on the system PATH. A developer command prompt adds them, which is why they
/// appear to be installed when you check from one - but a GUI application
/// launched from Explorer inherits the plain registry PATH and finds nothing.
///
/// So a bare "cmake" fails with "the system cannot find the file specified" on a
/// machine that plainly does have CMake, sitting inside Visual Studio.
///
/// The compiler itself needs no such help: CMake's Visual Studio generator
/// locates MSVC through the installation rather than the PATH, so a build works
/// without a developer environment once cmake itself has been found.
/// </summary>
public static class ToolchainLocator
{
    /// <summary>Where each tool sits relative to a Visual Studio installation.</summary>
    private const string CMakeRelativePath =
        @"Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe";

    private const string CTestRelativePath =
        @"Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\ctest.exe";

    private const string MsBuildRelativePath =
        @"MSBuild\Current\Bin\MSBuild.exe";

    public static string? FindCMake(string? configured) =>
        Resolve(configured, "cmake", CMakeRelativePath);

    /// <summary>
    /// ctest, which ships in the same folder as cmake. Takes the *cmake*
    /// setting, because that is what the user configures: if they pointed at a
    /// particular cmake.exe, its sibling ctest is the matching one, and running
    /// a ctest from a different installation against its build folder is asking
    /// for a version mismatch.
    /// </summary>
    public static string? FindCTest(string? configuredCMakePath)
    {
        if (!string.IsNullOrWhiteSpace(configuredCMakePath))
        {
            var cmake = configuredCMakePath.Trim();

            if (cmake.Contains(Path.DirectorySeparatorChar) || cmake.Contains(Path.AltDirectorySeparatorChar))
            {
                var sibling = Path.Combine(
                    Path.GetDirectoryName(cmake) ?? string.Empty,
                    OperatingSystem.IsWindows() ? "ctest.exe" : "ctest");

                return File.Exists(sibling) ? sibling : null;
            }
        }

        return Resolve(null, "ctest", CTestRelativePath);
    }

    public static string? FindMsBuild(string? configured) =>
        Resolve(configured, "msbuild", MsBuildRelativePath);

    /// <summary>
    /// The path to use, or null when the tool cannot be found anywhere - which
    /// the caller reports rather than launching something doomed to fail with a
    /// message that does not say what was missing.
    ///
    /// An explicit setting wins over everything, then the PATH, then Visual
    /// Studio. That order means configuring a path always does what it says, and
    /// somebody who installed CMake themselves keeps getting their own copy.
    /// </summary>
    public static string? Resolve(string? configured, string bareName, string relativeToVisualStudio)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath = configured.Trim();

            // A bare name in the box means "look for it" rather than a path.
            if (explicitPath.Contains(Path.DirectorySeparatorChar) ||
                explicitPath.Contains(Path.AltDirectorySeparatorChar))
            {
                return File.Exists(explicitPath) ? explicitPath : null;
            }

            bareName = explicitPath;
        }

        var onPath = FindOnPath(bareName);

        if (onPath != null)
            return onPath;

        foreach (var installation in VisualStudioInstallations.Value)
        {
            var candidate = Path.Combine(installation, relativeToVisualStudio);

            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    /// <summary>The tool as found on PATH, or null. Adds .exe on Windows.</summary>
    public static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH");

        if (string.IsNullOrEmpty(path))
            return null;

        var names = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? new[] { name + ".exe", name }
            : new[] { name };

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            foreach (var candidate in names)
            {
                try
                {
                    var full = Path.Combine(directory.Trim(), candidate);

                    if (File.Exists(full))
                        return full;
                }
                catch (ArgumentException)
                {
                    // A malformed PATH entry, of which real machines have plenty.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Every Visual Studio installation, worked out once - they do not appear or
    /// vanish while the app is running, and asking costs a process launch.
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> VisualStudioInstallations =
        new(FindVisualStudioInstallations);

    private static IReadOnlyList<string> FindVisualStudioInstallations()
    {
        var found = QueryVsWhere();

        // vswhere is installed alongside Visual Studio and is the supported way
        // to ask, but it can be absent or broken; the standard locations cover
        // the ordinary case when it is.
        if (found.Count == 0)
            found = GuessStandardLocations();

        return found;
    }

    /// <summary>
    /// Asks vswhere, which ships at a fixed path with the Visual Studio
    /// installer and knows about installations in non-default locations.
    ///
    /// Deliberately not "-latest": that returns a single installation, and on a
    /// machine with both Build Tools and Community it can pick the one whose
    /// C++ workload is not installed. Every installation is searched instead.
    /// </summary>
    private static List<string> QueryVsWhere()
    {
        var installations = new List<string>();

        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");

        if (!File.Exists(vswhere))
            return installations;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = vswhere,
                ArgumentList = { "-all", "-products", "*", "-prerelease", "-property", "installationPath" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process == null)
                return installations;

            var output = process.StandardOutput.ReadToEnd();

            if (!process.WaitForExit(10_000))
                return installations;

            foreach (var line in output.Split('\n'))
            {
                var trimmed = line.Trim();

                if (trimmed.Length > 0 && Directory.Exists(trimmed))
                    installations.Add(trimmed);
            }
        }
        catch (Exception)
        {
            // Not being able to ask is not an error worth surfacing; the caller
            // falls back and, failing that, reports the tool as missing.
        }

        return installations;
    }

    /// <summary>The default install roots, for when vswhere cannot be asked.</summary>
    private static List<string> GuessStandardLocations()
    {
        var installations = new List<string>();

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            var visualStudio = Path.Combine(root, "Microsoft Visual Studio");

            if (!Directory.Exists(visualStudio))
                continue;

            try
            {
                // ...\Microsoft Visual Studio\<year or version>\<edition>
                foreach (var version in Directory.EnumerateDirectories(visualStudio))
                foreach (var edition in Directory.EnumerateDirectories(version))
                    installations.Add(edition);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                // Skip what cannot be listed.
            }
        }

        return installations;
    }

    /// <summary>
    /// Says what was looked for and where, so a missing tool can be fixed rather
    /// than guessed at. "cmake was not found" on a machine with Visual Studio
    /// installed is a confusing thing to be told without this.
    /// </summary>
    public static string NotFoundMessage(string toolName, string settingName) =>
        $"'{toolName}' could not be found. It is not on the system PATH and no " +
        $"Visual Studio installation containing it was found. Visual Studio bundles " +
        $"{toolName} but does not add it to the PATH, so this can happen on a machine " +
        $"where it plainly is installed. Set the full path under " +
        $"Settings - Build tools - {settingName}, or install {toolName} and add it to the PATH.";
}
