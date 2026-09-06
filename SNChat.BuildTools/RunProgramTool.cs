using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Services;
using SNChat.Core.Tools;

namespace SNChat.BuildTools;

/// <summary>
/// Runs a program and hands back what it printed.
///
/// This is what closes the loop. Before it, the assistant could write code and
/// compile it but never learn whether it worked - it built well_done.exe and
/// never saw "well done" - so every session ended by asking the user to run the
/// thing and paste the output back.
///
/// Confined to the same folders as the build tools, so in practice the model
/// runs something it just compiled there. That is a real step beyond building:
/// a build executes scripts the project's author wrote, this executes a binary
/// the model itself produced.
/// </summary>
public class RunProgramTool : ITool
{
    /// <summary>
    /// What may be launched. Scripts are included because a build legitimately
    /// produces them, and they are no more dangerous than the .exe beside them -
    /// both live inside a folder the user allowed.
    /// </summary>
    private static readonly string[] RunnableExtensions =
        { ".exe", ".com", ".bat", ".cmd" };

    private readonly SettingsService _settingsService;
    private readonly ProjectContext _projects;
    private readonly ProcessRunner _runner;
    private readonly ILogger<RunProgramTool> _logger;

    public string Name => "run_program";

    public string Description =>
        "Run a program and return what it printed, plus its exit code. Use this " +
        "after build_project to check that the program actually works, and to " +
        "read its output when it does not. The program must be inside an allowed " +
        "project folder - normally something you just built there.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["path"] = new()
            {
                Type = "string",
                Description = "Full path to the executable to run. Must be inside an " +
                              "allowed project folder."
            },
            ["arguments"] = new()
            {
                Type = "array",
                Description = "Command-line arguments, one per element. Omit if none.",
                Items = new ToolParameterProperty { Type = "string" }
            },
            ["stdin"] = new()
            {
                Type = "string",
                Description = "Text to feed the program's standard input. Omit if the " +
                              "program does not read input."
            }
        },
        Required = new List<string> { "path" }
    };

    public RunProgramTool(
        SettingsService settingsService,
        ProjectContext projects,
        ProcessRunner runner,
        ILogger<RunProgramTool> logger)
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

        if (!settings.AllowRun)
            return "Running programs is turned off. Enable it under Settings - Build tools.";

        if (!arguments.TryGetValue("path", out var rawPath) || rawPath is null)
            return "Error: no 'path' argument was provided.";

        var guard = new WorkspaceGuard(_projects.EffectiveRoots(settings));
        var requested = rawPath.ToString() ?? string.Empty;
        var resolved = guard.Resolve(requested);

        if (resolved == null)
            return guard.DenialMessage(requested);

        if (!File.Exists(resolved))
            return $"There is no file at '{resolved}'. Build the project first, then run " +
                   "the executable the build produced.";

        var extension = Path.GetExtension(resolved);

        if (!RunnableExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return $"'{Path.GetFileName(resolved)}' is not something this tool will run. " +
                   $"Expected one of {string.Join(", ", RunnableExtensions)}.";
        }

        var programArguments = ReadArguments(arguments);
        var standardInput = arguments.TryGetValue("stdin", out var raw) ? raw?.ToString() : null;

        _logger.LogInformation("Running {Path} with {Count} argument(s)",
            resolved, programArguments.Count);

        var result = await _runner.RunAsync(
            resolved,
            programArguments,
            // Its own folder, so a program that reads files beside it behaves the
            // way it would if the user had double-clicked it.
            Path.GetDirectoryName(resolved) ?? resolved,
            TimeSpan.FromSeconds(Math.Clamp(settings.RunTimeoutSeconds, 5, 3600)),
            cancellationToken,
            standardInput);

        return Describe(result, Path.GetFileName(resolved));
    }

    /// <summary>
    /// The arguments as strings. Anything that is not a list is ignored rather
    /// than guessed at - a model that sends a single string instead of an array
    /// should be told what it produced, not have it silently reinterpreted.
    /// </summary>
    private static List<string> ReadArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("arguments", out var raw) || raw is null)
            return new List<string>();

        if (raw is IEnumerable<object?> items)
            return items.Select(item => item?.ToString() ?? string.Empty).ToList();

        return new List<string>();
    }

    /// <summary>
    /// What the model reads. Raw output rather than parsed diagnostics - for a
    /// program run the text it printed *is* the answer - but capped, because a
    /// chatty program would otherwise fill the context window in one call.
    /// </summary>
    public static string Describe(ProcessResult result, string name)
    {
        if (!result.Started)
            return $"{name} could not start: {result.StartupError}";

        var report = new StringBuilder();

        if (result.TimedOut)
        {
            report.AppendLine(
                $"{name} was still running when its time ran out and was stopped. " +
                "If it was waiting for input, pass it with 'stdin'.");
        }
        else if (result.ExitCode == 0)
        {
            report.AppendLine($"{name} finished successfully.");
        }
        else
        {
            report.AppendLine($"{name} exited with code {DescribeExitCode(result.ExitCode)}.");
        }

        var output = Trim(result.Output);

        if (output.Length == 0)
        {
            report.AppendLine("It printed nothing.");
        }
        else
        {
            report.AppendLine();
            report.AppendLine("Output:");
            report.AppendLine(output);
        }

        // Worth saying explicitly: a program that printed its complaint on
        // stderr and still exited 0 is easy to misread as having worked.
        if (result.ExitCode == 0 && result.StandardError.Trim().Length > 0)
            report.AppendLine("(Some of that output was written to the error stream.)");

        return report.ToString().TrimEnd();
    }

    /// <summary>
    /// Windows reports a crash as a large negative exit code that means nothing
    /// on its own. These four are the ones a C or C++ session actually produces.
    /// </summary>
    public static string DescribeExitCode(int exitCode)
    {
        var meaning = unchecked((uint)exitCode) switch
        {
            0xC0000005 => "access violation - a null or invalid pointer",
            0xC0000374 => "heap corruption - something wrote past the end of a buffer",
            0xC00000FD => "stack overflow - most often runaway recursion",
            0xC000013A => "terminated by Ctrl+C",
            0xC0000094 => "integer division by zero",
            0xC0000409 => "stack buffer overrun detected by the runtime",
            _ => null
        };

        return meaning == null
            ? exitCode.ToString()
            : $"0x{unchecked((uint)exitCode):X8} ({meaning})";
    }

    /// <summary>
    /// Keeps the beginning and the end. A usage message is at the top and a
    /// crash is at the bottom, and both matter, so the middle is what goes.
    /// </summary>
    public static string Trim(string output, int headLines = 80, int tailLines = 80)
    {
        var lines = output.Split('\n').Select(line => line.TrimEnd('\r')).ToList();

        while (lines.Count > 0 && lines[^1].Trim().Length == 0)
            lines.RemoveAt(lines.Count - 1);

        if (lines.Count <= headLines + tailLines)
            return string.Join("\n", lines);

        var kept = new List<string>();
        kept.AddRange(lines.Take(headLines));
        kept.Add($"... [{lines.Count - headLines - tailLines} lines omitted] ...");
        kept.AddRange(lines.Skip(lines.Count - tailLines));

        return string.Join("\n", kept);
    }
}
