using System.Text;
using System.Text.RegularExpressions;

namespace SNChat.BuildTools;

public enum DiagnosticSeverity
{
    Warning,
    Error
}

/// <summary>One compiler complaint, reduced to what a fix needs.</summary>
public sealed record BuildDiagnostic(
    DiagnosticSeverity Severity,
    string File,
    int Line,
    int Column,
    string Code,
    string Message)
{
    /// <summary>Reads as "Program.cs(42,17): CS0103: the name 'foo' does not exist".</summary>
    public override string ToString()
    {
        var where = File.Length == 0 ? string.Empty
            : Line > 0 ? $"{File}({Line},{Column}): "
            : $"{File}: ";

        var code = Code.Length == 0 ? string.Empty : $"{Code}: ";

        return where + code + Message;
    }
}

/// <summary>
/// Pulls the errors and warnings out of a build log.
///
/// A build prints far more than it needs to - restore notices, project headers,
/// per-file progress - and handing all of it back would swamp the context window
/// while burying the three lines that matter. This keeps the diagnostics and
/// throws the rest away.
/// </summary>
public static class BuildOutputParser
{
    /// <summary>
    /// MSBuild, and so csc, vbc and every task that follows their convention:
    ///
    ///   C:\src\Program.cs(42,17): error CS0103: The name 'foo' does not exist
    ///   C:\src\App.csproj : error NU1101: Unable to find package Foo
    ///
    /// The position is optional because project-level errors carry none, and the
    /// code is optional because some tasks emit a bare "error: ..." instead.
    /// </summary>
    private static readonly Regex MsBuildPattern = new(
        @"^\s*(?<file>[^\s].*?)\s*(?:\((?<line>\d+)(?:,(?<col>\d+))?\))?\s*:\s*(?<sev>error|warning)\s*(?<code>[A-Za-z]+[0-9]+)?\s*:\s*(?<msg>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The colon-separated shape, used by javac as Gradle relays it and by
    /// gcc and clang:
    ///
    ///   /src/Main.java:12: error: cannot find symbol
    ///   /src/main.cpp:5:3: error: 'foo' was not declared in this scope
    ///   /src/main.cpp:5:3: fatal error: iostream: No such file or directory
    ///
    /// Distinguished from the MSBuild shape by the colon before the line number
    /// rather than parentheses around it. The column is optional because javac
    /// omits it and the C compilers do not.
    /// </summary>
    private static readonly Regex ColonPositionPattern = new(
        @"^\s*(?<file>(?:[A-Za-z]:)?[^\s:][^:]*?):(?<line>\d+)(?::(?<col>\d+))?:\s*(?<sev>fatal error|error|warning)\s*:\s*(?<msg>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// CMake's own complaints, which have no compiler behind them:
    ///
    ///   CMake Error at CMakeLists.txt:5 (add_executable):
    /// </summary>
    private static readonly Regex CMakePattern = new(
        @"^\s*CMake\s+(?<sev>Error|Warning)(?:\s+at\s+(?<file>[^\s:]+):(?<line>\d+))?\s*(?:\([^)]*\))?\s*:?\s*(?<msg>.*)$",
        RegexOptions.Compiled);

    /// <summary>
    /// Gradle's own failures, which carry no file at all:
    ///
    ///   * What went wrong:
    ///   Execution failed for task ':app:compileDebugJavaWithJavac'.
    /// </summary>
    private static readonly Regex GradleFailurePattern = new(
        @"^\s*(?:FAILURE:\s*(?<msg1>.+?)|Execution failed for task\s*'(?<task>[^']+)'\.?)\s*$",
        RegexOptions.Compiled);

    public static IReadOnlyList<BuildDiagnostic> Parse(string output)
    {
        var found = new List<BuildDiagnostic>();

        if (string.IsNullOrEmpty(output))
            return found;

        // MSBuild repeats every diagnostic in its end-of-build summary, and a
        // project built for several target frameworks repeats it once per
        // framework. Without this the model reads the same error three times and
        // concludes there are three problems.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0)
                continue;

            // Order matters. The colon shape goes first because the MSBuild
            // pattern is loose enough to match it too, taking the ":12" into the
            // filename and losing the line number with it. CMake's own lines go
            // before MSBuild's for the same reason.
            var diagnostic = MatchColonPosition(line)
                ?? MatchCMake(line)
                ?? MatchMsBuild(line)
                ?? MatchGradle(line);

            if (diagnostic != null && seen.Add(diagnostic.ToString()))
                found.Add(diagnostic);
        }

        return found;
    }

    private static BuildDiagnostic? MatchMsBuild(string line)
    {
        var match = MsBuildPattern.Match(line);

        if (!match.Success)
            return null;

        var file = match.Groups["file"].Value;

        // "error" and "warning" appear in ordinary prose too, and a line whose
        // "file" is a whole sentence is prose rather than a diagnostic.
        if (file.Contains(' ') && !Path.IsPathRooted(file) && !file.Contains('.'))
            return null;

        return new BuildDiagnostic(
            Severity(match.Groups["sev"].Value),
            file,
            ParseInt(match.Groups["line"].Value),
            ParseInt(match.Groups["col"].Value),
            match.Groups["code"].Value,
            StripProjectSuffix(match.Groups["msg"].Value));
    }

    /// <summary>
    /// Removes the "[C:\...\thing.vcxproj]" that MSBuild appends to every
    /// diagnostic to say which project it came from. Repeated on every line and
    /// often longer than the message, it is pure cost against the context window.
    /// </summary>
    private static string StripProjectSuffix(string message)
    {
        var suffix = ProjectSuffixPattern.Match(message);

        return suffix.Success
            ? message[..suffix.Index].TrimEnd()
            : message;
    }

    private static readonly Regex ProjectSuffixPattern = new(
        @"\s*\[[^\]]*\.(?:vcx|cs|vb|fs|sln)proj\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static BuildDiagnostic? MatchColonPosition(string line)
    {
        var match = ColonPositionPattern.Match(line);

        if (!match.Success)
            return null;

        return new BuildDiagnostic(
            Severity(match.Groups["sev"].Value),
            match.Groups["file"].Value.Trim(),
            ParseInt(match.Groups["line"].Value),
            ParseInt(match.Groups["col"].Value),
            string.Empty,
            match.Groups["msg"].Value);
    }

    private static BuildDiagnostic? MatchCMake(string line)
    {
        var match = CMakePattern.Match(line);

        if (!match.Success)
            return null;

        var message = match.Groups["msg"].Value.Trim();

        // "CMake Error at CMakeLists.txt:5 (add_executable):" carries its detail
        // on the following lines, so the header alone is still worth reporting -
        // it names the file and line, which is what a fix needs.
        if (message.Length == 0)
            message = "see the CMake output for details";

        return new BuildDiagnostic(
            Severity(match.Groups["sev"].Value),
            match.Groups["file"].Value,
            ParseInt(match.Groups["line"].Value),
            0,
            "CMake",
            message);
    }

    private static BuildDiagnostic? MatchGradle(string line)
    {
        var match = GradleFailurePattern.Match(line);

        if (!match.Success)
            return null;

        var task = match.Groups["task"].Value;

        var message = task.Length > 0
            ? $"Gradle task '{task}' failed"
            : match.Groups["msg1"].Value;

        if (string.IsNullOrWhiteSpace(message))
            return null;

        return new BuildDiagnostic(
            DiagnosticSeverity.Error, string.Empty, 0, 0, string.Empty, message);
    }

    /// <summary>
    /// Contains rather than equals, so gcc's "fatal error" is not filed as a
    /// warning - which would hide the one diagnostic that stopped the build.
    /// </summary>
    private static DiagnosticSeverity Severity(string value) =>
        value.Contains("error", StringComparison.OrdinalIgnoreCase)
            ? DiagnosticSeverity.Error
            : DiagnosticSeverity.Warning;

    private static int ParseInt(string value) =>
        int.TryParse(value, out var parsed) ? parsed : 0;

    /// <summary>
    /// The report handed back to the model: a verdict, then the errors, then a
    /// few warnings. Errors are given far more room than warnings because a
    /// failing build is what it was asked to fix, and a clean build's warnings
    /// are rarely why it was run.
    /// </summary>
    public static string Summarize(
        ProcessResult result,
        string what,
        int maxErrors = 40,
        int maxWarnings = 10)
    {
        if (!result.Started)
            return $"{what} could not start: {result.StartupError}";

        var diagnostics = Parse(result.Output);
        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList();

        var report = new StringBuilder();

        if (result.TimedOut)
            report.AppendLine($"{what} ran out of time and was stopped.");
        else if (result.Succeeded)
            report.AppendLine($"{what} succeeded.");
        else
            report.AppendLine($"{what} failed (exit code {result.ExitCode}).");

        report.AppendLine($"{errors.Count} error(s), {warnings.Count} warning(s).");

        Append(report, "Errors", errors, maxErrors);
        Append(report, "Warnings", warnings, maxWarnings);

        // A build that fails while printing nothing recognisable would otherwise
        // come back as "failed" with no clue why, which the model cannot act on.
        if (errors.Count == 0 && !result.Succeeded)
        {
            report.AppendLine();
            report.AppendLine("No diagnostics were recognised. The last lines of output were:");
            report.AppendLine(Tail(result.Output, 25));
        }

        return report.ToString().TrimEnd();
    }

    private static void Append(
        StringBuilder report,
        string heading,
        IReadOnlyList<BuildDiagnostic> diagnostics,
        int limit)
    {
        if (diagnostics.Count == 0)
            return;

        report.AppendLine();
        report.AppendLine($"{heading}:");

        foreach (var diagnostic in diagnostics.Take(limit))
            report.AppendLine($"  {diagnostic}");

        if (diagnostics.Count > limit)
            report.AppendLine($"  ... and {diagnostics.Count - limit} more");
    }

    /// <summary>The last few lines, for when nothing parsed.</summary>
    public static string Tail(string output, int lines)
    {
        var all = output.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.Trim().Length > 0)
            .ToList();

        return string.Join("\n", all.Skip(Math.Max(0, all.Count - lines)));
    }
}
