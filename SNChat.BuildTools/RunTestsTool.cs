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
            }
        },
        Required = new List<string> { "path" }
    };

    public RunTestsTool(
        SettingsService settingsService,
        ProcessRunner runner,
        ILogger<RunTestsTool> logger)
    {
        _settingsService = settingsService;
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

        var resolved = BuildToolArguments.Resolve(arguments, settings, out var failure);

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

        var result = await _runner.RunAsync(
            fileName,
            args,
            target.WorkingDirectory,
            TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 10, 3600)),
            cancellationToken);

        return Summarize(result, Path.GetFileName(target.Path));
    }

    /// <summary>
    /// Test runners report a pass/fail tally that the compiler diagnostics
    /// summary knows nothing about, so the counts and the failing test names are
    /// picked out on top of the usual build errors - a suite that would not
    /// compile still needs its compiler errors shown.
    /// </summary>
    internal static string Summarize(ProcessResult result, string what)
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

    private static List<string> FailedTests(string output)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var match = FailedTestPattern.Match(line);

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
