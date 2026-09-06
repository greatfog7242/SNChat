using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SNChat.BuildTools;

/// <summary>What a build or test command produced.</summary>
public sealed class ProcessResult
{
    public int ExitCode { get; init; }
    public string Output { get; init; } = string.Empty;

    /// <summary>True when the command was still running when its time ran out.</summary>
    public bool TimedOut { get; init; }

    /// <summary>Set when the command could not be started at all.</summary>
    public string? StartupError { get; init; }

    public bool Started => StartupError == null;
    public bool Succeeded => Started && !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs one build command and collects what it printed.
///
/// Deliberately not a shell: the executable and each argument are passed
/// separately, so nothing the model supplies can be read as a second command.
/// Going through cmd.exe would make "; rm -rf" in a project name into an
/// injection, and there is nothing a build needs that a shell provides.
/// </summary>
public sealed class ProcessRunner
{
    /// <summary>
    /// A cap on what is kept in memory, well above what any summary shows. A
    /// Gradle build with a wall of deprecation notices can print megabytes, and
    /// none of it is worth holding once the diagnostics have been read out.
    /// </summary>
    public const int MaxCapturedCharacters = 512 * 1024;

    private readonly ILogger<ProcessRunner> _logger;

    public ProcessRunner(ILogger<ProcessRunner> logger)
    {
        _logger = logger;
    }

    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        // Build tools colour their output and draw progress bars when they think
        // a terminal is watching, which turns the log into escape codes.
        startInfo.Environment["NO_COLOR"] = "1";
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["TERM"] = "dumb";

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

        var captured = new StringBuilder();
        var truncated = false;

        void Capture(string? line)
        {
            if (line == null)
                return;

            lock (captured)
            {
                if (captured.Length >= MaxCapturedCharacters)
                {
                    truncated = true;
                    return;
                }

                captured.AppendLine(line);
            }
        }

        // stderr is folded in with stdout: MSBuild and Gradle both split
        // diagnostics across the two, and a build's errors are the point.
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start {FileName}", fileName);
            return new ProcessResult { StartupError = $"could not run '{fileName}': {ex.Message}" };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);

        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            KillTree(process, fileName);

            // The user cancelling and the command overrunning look identical
            // here; only the first should abort the whole reply.
            cancellationToken.ThrowIfCancellationRequested();

            return new ProcessResult
            {
                ExitCode = -1,
                TimedOut = true,
                Output = Read()
            };
        }

        // Lets the last buffered lines arrive; without it a fast-failing build
        // can exit before its own error text has been read.
        process.WaitForExit();

        var output = Read();

        if (truncated)
            output += "\n[output truncated]";

        return new ProcessResult { ExitCode = process.ExitCode, Output = output };

        string Read()
        {
            lock (captured)
                return captured.ToString();
        }
    }

    /// <summary>
    /// Kills the children too. MSBuild and Gradle both leave long-lived worker
    /// and daemon processes behind, and killing only the launcher would leave
    /// those holding the project's files locked.
    /// </summary>
    private void KillTree(Process process, string fileName)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop {FileName} after it overran", fileName);
        }
    }
}
