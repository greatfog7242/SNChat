using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.BuildTools;

/// <summary>
/// Runs a project's test suite and reports which tests failed and why.
///
/// Separately switchable from building, because this runs the project's own code
/// rather than only compiling it - a test can do anything the user can.
/// </summary>
public class RunTestsTool : ITool
{
    private readonly SettingsService _settingsService;
    private readonly ProjectContext _projects;
    private readonly ProcessRunner _runner;
    private readonly ILogger<RunTestsTool> _logger;

    public string Name => "run_tests";

    public string Description =>
        "Run a project's tests and report which failed. Handles .NET " +
        "(dotnet test), C/C++ with CMake (ctest, after build_project has run), " +
        "and Android/Gradle. Use it to check whether a change broke anything, " +
        "once build_project shows the code compiles.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["path"] = new()
            {
                Type = "string",
                Description = "Full path to the solution, test project, or the folder " +
                              "containing one. Must be inside an allowed project folder."
            },
            ["configuration"] = new()
            {
                Type = "string",
                Description = "Which configuration to test. Defaults to Debug.",
                Enum = new List<string> { "Debug", "Release" }
            },
            ["test"] = new()
            {
                Type = "string",
                Description = "Run only tests whose name contains this. Use it to re-run a " +
                              "single failing test and read its assertion message and stack " +
                              "trace. Omit to run the whole suite."
            }
        },
        Required = new List<string> { "path" }
    };

    public RunTestsTool(
        SettingsService settingsService,
        ProjectContext projects,
        ProcessRunner runner,
        ILogger<RunTestsTool> logger)
    {
        _settingsService = settingsService;
        _projects = projects;
        _runner = runner;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetCachedSettings().BuildTools;

        if (!settings.AllowTests)
            return "Running tests is turned off. Enable it under Settings - Build tools.";

        var resolved = BuildToolArguments.Resolve(arguments, settings, _projects, out var failure);

        if (resolved == null)
            return failure!;

        var (target, configuration) = resolved.Value;

        _logger.LogInformation("Testing {Kind} target {Path} ({Configuration})",
            target.Kind, target.Path, configuration);

        // ctest ships beside cmake inside Visual Studio, so it is no more on the
        // PATH than cmake is.
        if (target.Kind == ProjectKind.CMake && ToolchainLocator.FindCTest(settings.CMakePath) == null)
            return ToolchainLocator.NotFoundMessage("ctest", "cmake executable");

        var (fileName, args) = target.Kind switch
        {
            ProjectKind.Gradle => (BuildToolArguments.GradleExecutable(target),
                new List<string> { "test", "--console=plain", "--no-daemon" }),

            // ctest reads the build folder cmake generated, so the project has
            // to have been built at least once for there to be tests to run.
            ProjectKind.CMake => (ToolchainLocator.FindCTest(settings.CMakePath)!, new List<string>
            {
                "--test-dir", target.CMakeBuildDirectory,
                "--output-on-failure",
                "-C", configuration
            }),

            _ => (settings.DotnetPath, new List<string>
            {
                "test", target.Path,
                "--configuration", configuration,
                "--nologo",
                "--verbosity", "minimal"
            })
        };

        // Each filter goes on as a single argument, so nothing in it can be read
        // as a second one - there is no shell here to re-split it.
        var filter = ReadFilter(arguments);

        if (filter != null)
        {
            args.AddRange(target.Kind switch
            {
                ProjectKind.Gradle => new[] { "--tests", $"*{filter}*" },
                ProjectKind.CMake => new[] { "-R", filter },
                _ => new[] { "--filter", $"FullyQualifiedName~{filter}" }
            });

            // Asking for one test means wanting to know why it failed, not that
            // it failed, and the assertion message only appears above minimal.
            var verbosity = args.IndexOf("--verbosity");

            if (verbosity >= 0 && verbosity + 1 < args.Count)
                args[verbosity + 1] = "normal";
        }

        var result = await _runner.RunAsync(
            fileName,
            args,
            target.WorkingDirectory,
            TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 3600)),
            cancellationToken);

        return Summarize(result, Path.GetFileName(target.Path), filter != null);
    }

    /// <summary>
    /// The test filter, or null. Whitespace-only is treated as absent rather
    /// than as a filter matching everything, which would silently run the suite
    /// while the model believed it had narrowed things down.
    /// </summary>
    private static string? ReadFilter(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("test", out var raw) || raw is null)
            return null;

        var filter = raw.ToString()?.Trim();

        return string.IsNullOrEmpty(filter) ? null : filter;
    }

    /// <summary>
    /// Test runners report a pass/fail tally that the compiler diagnostics
    /// summary knows nothing about, so the counts and the failing test names are
    /// picked out on top of the usual build errors - a suite that would not
    /// compile still needs its compiler errors shown.
    /// </summary>
    public static string Summarize(ProcessResult result, string what, bool detailed = false)
    {
        if (!result.Started)
            return $"Tests for {what} could not start: {result.StartupError}";

        var report = new StringBuilder();

        if (result.TimedOut)
            report.AppendLine($"Tests for {what} ran out of time and were stopped.");
        else if (result.Succeeded)
            report.AppendLine($"Tests for {what} passed.");
        else
            report.AppendLine($"Tests for {what} failed (exit code {result.ExitCode}).");

        var tally = TallyPattern.Match(result.Output);

        if (tally.Success)
            report.AppendLine(tally.Value.Trim());

        var failures = FailedTests(result.Output);

        if (failures.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("Failed tests:");

            foreach (var failure in failures.Take(25))
                report.AppendLine($"  {failure}");

            if (failures.Count > 25)
                report.AppendLine($"  ... and {failures.Count - 25} more");

            // Only when a specific test was asked for. Across a whole suite the
            // names are what is wanted; for one test the assertion is the point,
            // and pasting every message from a broad run would swamp the context.
            var detail = FailureDetail(result.Output);

            if (detailed && detail.Length > 0)
            {
                report.AppendLine();
                report.AppendLine("Why it failed:");
                report.AppendLine(detail);
            }
        }

        // A suite that did not compile fails with no failing tests at all; its
        // compiler errors are the useful part.
        if (failures.Count == 0 && !result.Succeeded)
        {
            var errors = BuildOutputParser.Parse(result.Output)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Take(20)
                .ToList();

            report.AppendLine();

            if (errors.Count > 0)
            {
                report.AppendLine("Errors:");
                foreach (var error in errors)
                    report.AppendLine($"  {error}");
            }
            else
            {
                report.AppendLine("No failing tests were identified. The last lines of output were:");
                report.AppendLine(BuildOutputParser.Tail(result.Output, 25));
            }
        }

        return report.ToString().TrimEnd();
    }

    /// <summary>
    /// The VSTest summary line, e.g.
    /// "Failed! - Failed: 2, Passed: 115, Skipped: 0, Total: 117".
    /// </summary>
    private static readonly Regex TallyPattern = new(
        @"(?:Failed|Passed)!\s*-\s*Failed:\s*\d+.*?Total:\s*\d+",
        RegexOptions.Compiled);

    /// <summary>
    /// Names of failing tests. xUnit and NUnit both print "[FAIL]" through
    /// VSTest, and dotnet test also prints a bare "  Failed Name [12 ms]".
    /// </summary>
    private static readonly Regex FailedTestPattern = new(
        @"^\s*(?:\[xUnit\.net[^\]]*\]\s*)?(?<name>[\w.+<>:` ]+?)\s*(?:\[FAIL\]|\bFailed\b(?:\s*\[[^\]]*\])?)\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// The shape `dotnet test` actually prints, where the verdict comes *first*:
    ///
    ///   Failed Proj.Tests.MathTests.Adds_two_numbers [3 ms]
    ///   X Proj.Tests.MathTests.Adds_two_numbers [3 ms]
    ///
    /// Missing this matched nothing, so no failing test was ever named and the
    /// summary fell through to dumping raw output instead.
    /// </summary>
    private static readonly Regex FailedTestLeadingPattern = new(
        @"^\s*(?:X|Failed)\s+(?<name>[\w.+<>:`]+(?:\([^)]*\))?)\s*(?:\[[^\]]*\])?\s*$",
        RegexOptions.Compiled);

    /// <summary>
    /// The assertion message and the top of the stack, which is what actually
    /// says why a test failed. VSTest prints these under "Error Message:" and
    /// "Stack Trace:" headings; ctest and Gradle print their own thing, so
    /// anything not matching those headings falls back to nothing rather than
    /// guessing at structure that is not there.
    ///
    /// Only the first few frames are kept: the failing assertion and the code
    /// under test are at the top, and everything below is the runner's own
    /// plumbing.
    /// </summary>
    public static string FailureDetail(string output, int maxLines = 30)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var kept = new List<string>();
        var capturing = false;
        var stackFrames = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.StartsWith("Error Message:", StringComparison.OrdinalIgnoreCase))
            {
                capturing = true;
                stackFrames = 0;
                kept.Add(trimmed);
                continue;
            }

            if (!capturing)
                continue;

            if (trimmed.StartsWith("Stack Trace:", StringComparison.OrdinalIgnoreCase))
            {
                kept.Add(trimmed);
                continue;
            }

            // A blank line after something has been captured ends the block.
            if (trimmed.Length == 0)
            {
                capturing = false;
                continue;
            }

            if (trimmed.StartsWith("at ", StringComparison.Ordinal) && ++stackFrames > 4)
                continue;

            kept.Add("  " + trimmed);

            if (kept.Count >= maxLines)
                break;
        }

        return string.Join("\n", kept);
    }

    private static List<string> FailedTests(string output)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            var match = FailedTestLeadingPattern.Match(line);

            if (!match.Success)
                match = FailedTestPattern.Match(line);

            if (!match.Success)
                continue;

            var name = match.Groups["name"].Value.Trim();

            // "Failed!" on its own is the tally line, not a test.
            if (name.Length == 0 || name.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                continue;

            if (seen.Add(name))
                names.Add(name);
        }

        return names;
    }
}
