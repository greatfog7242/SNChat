using SNChat.Core.Models;

namespace SNChat.BuildTools;

/// <summary>A command to run, or the reason there is not one.</summary>
public sealed record ToolchainCommand(string Executable, List<string> Arguments)
{
    /// <summary>Set instead of a command when the tool could not be found or does not apply.</summary>
    public string? Problem { get; init; }

    public static ToolchainCommand Missing(string problem) =>
        new(string.Empty, new List<string>()) { Problem = problem };

    public bool CanRun => Problem == null && Executable.Length > 0;
}

/// <summary>
/// What "build" and "test" mean for each kind of project.
///
/// Kept apart from the tools so that adding a language is a table entry rather
/// than another branch inside a method that already handles five. Every
/// executable is resolved through <see cref="ToolchainLocator"/> rather than
/// named bare, because interpreters have the same problem cmake had: installed,
/// working, and not on the PATH a GUI app inherits.
/// </summary>
public static class Toolchains
{
    public static ToolchainCommand Build(
        BuildTarget target,
        string configuration,
        BuildToolSettings settings) => target.Kind switch
    {
        ProjectKind.Maven => Maven(new List<string> { "-q", "compile" }),
        ProjectKind.Node => Npm(new List<string> { "run", "build" }),

        // Python has no build step. Compiling to bytecode is the nearest
        // equivalent and is genuinely useful: it reports syntax errors across
        // the whole tree, which is what "does this compile" means here.
        ProjectKind.Python => Python(new List<string>
        {
            "-m", "compileall", "-q", target.WorkingDirectory
        }),

        // Not a build so much as making the project runnable at all; without
        // its gems nothing else will work.
        ProjectKind.Ruby => Bundle(new List<string> { "install" }),

        _ => ToolchainCommand.Missing($"No build command is known for a {target.Kind} project.")
    };

    public static ToolchainCommand Test(
        BuildTarget target,
        string configuration,
        BuildToolSettings settings) => target.Kind switch
    {
        ProjectKind.Maven => Maven(new List<string> { "-q", "test" }),
        ProjectKind.Node => Npm(new List<string> { "test" }),

        // Run through the interpreter rather than as "pytest", so it works
        // without pytest's own launcher script being on the PATH.
        ProjectKind.Python => Python(new List<string> { "-m", "pytest" }),

        ProjectKind.Ruby => Bundle(ProjectLocator.IsRails(target)
            ? new List<string> { "exec", "rails", "test" }
            : new List<string> { "exec", "rspec" }),

        _ => ToolchainCommand.Missing($"No test command is known for a {target.Kind} project.")
    };

    /// <summary>
    /// Whether run_program can start this kind of project at all, and what to
    /// say when it cannot. Both cases here are real limitations rather than
    /// gaps to be filled in later.
    /// </summary>
    public static string? RunLimitation(ProjectKind kind) => kind switch
    {
        // Deploying to a device or emulator is adb's job, not a matter of
        // launching a process and reading its output.
        ProjectKind.Gradle =>
            "An Android app is run by installing it on a device or emulator with adb, " +
            "which this tool does not do. Building and testing work.",

        // A server never exits, so there is nothing to wait for and no exit code
        // to report. run_program is built for programs that finish.
        ProjectKind.Ruby =>
            "A Rails server does not exit, so it cannot be run this way. Run a script " +
            "with run_program, or use run_tests.",

        _ => null
    };

    private static ToolchainCommand Python(List<string> arguments)
    {
        // Both names exist in the wild and which one is present depends on the
        // installer, so neither can be assumed.
        var python = ToolchainLocator.FindOnPathAny("python", "python3");

        return python == null
            ? ToolchainCommand.Missing(
                "Python could not be found. Install it, or add it to the PATH.")
            : new ToolchainCommand(python, arguments);
    }

    private static ToolchainCommand Npm(List<string> arguments)
    {
        // npm on Windows is npm.cmd, which FindOnPath will not match by the bare
        // name, so both spellings are tried.
        var npm = ToolchainLocator.FindOnPathAny("npm.cmd", "npm");

        return npm == null
            ? ToolchainCommand.Missing(
                "npm could not be found. Install Node.js, or add it to the PATH.")
            : new ToolchainCommand(npm, arguments);
    }

    private static ToolchainCommand Maven(List<string> arguments)
    {
        var mvn = ToolchainLocator.FindOnPathAny("mvn.cmd", "mvn");

        return mvn == null
            ? ToolchainCommand.Missing(
                "Maven (mvn) could not be found. Install it, or add it to the PATH. " +
                "A JDK alone is not enough to build a pom.xml project.")
            : new ToolchainCommand(mvn, arguments);
    }

    private static ToolchainCommand Bundle(List<string> arguments)
    {
        var bundle = ToolchainLocator.FindOnPathAny("bundle.bat", "bundle");

        return bundle == null
            ? ToolchainCommand.Missing(
                "Bundler could not be found. Install Ruby and run 'gem install bundler'.")
            : new ToolchainCommand(bundle, arguments);
    }
}
