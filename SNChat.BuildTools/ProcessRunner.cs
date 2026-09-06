using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace SNChat.BuildTools;

/// <summary>What a build or test command produced.</summary>
public sealed class ProcessResult
{
    public int ExitCode { get; init; }

    /// <summary>
    /// Everything the process printed, both streams interleaved in the order it
    /// actually wrote them. This is what a build's diagnostics are read from.
    /// </summary>
    public string Output { get; init; } = string.Empty;

    /// <summary>
    /// Just the error stream. Kept alongside <see cref="Output"/> rather than
    /// instead of it, because debugging a program wants to know *which* stream
    /// said something while a build only wants the text in order.
    /// </summary>
    public string StandardError { get; init; } = string.Empty;

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

    /// <summary>
    /// <paramref name="standardInput"/> is written to the process and the stream
    /// is then closed. Null means "nothing to send", which still closes it - see
    /// the note where that happens.
    /// </summary>
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        string workingDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        string? standardInput = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Always redirected, never inherited. A GUI app has no console to
            // inherit, and a redirected-then-closed stdin is what stops a
            // program that reads input from waiting forever on one that will
            // never arrive.
            RedirectStandardInput = true,
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
        var errors = new StringBuilder();
        var truncated = false;

        void Capture(string? line, bool isError)
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

                if (isError)
                    errors.AppendLine(line);
            }
        }

        // stderr is folded in with stdout so the order survives: MSBuild and
        // Gradle both split diagnostics across the two, and a build's errors are
        // the point. It is also kept on its own, for callers that care which
        // stream a line came from.
        process.OutputDataReceived += (_, e) => Capture(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => Capture(e.Data, isError: true);

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

        // Send whatever was supplied, then close - always, even with nothing to
        // send. A program that reads until end-of-input hangs forever on a stdin
        // that stays open, and burns the entire timeout doing nothing.
        try
        {
            if (!string.IsNullOrEmpty(standardInput))
                await process.StandardInput.WriteAsync(standardInput);

            process.StandardInput.Close();
        }
        catch (IOException ex)
        {
            // A process that has already exited closes the pipe first. That is
            // not a failure of the run - its output is still worth reading.
            _logger.LogDebug(ex, "Could not write stdin to {FileName}; it may have exited already", fileName);
        }

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
                Output = Read(),
                StandardError = ReadErrors()
            };
        }

        // Lets the last buffered lines arrive; without it a fast-failing build
        // can exit before its own error text has been read.
        process.WaitForExit();

        var output = Read();

        if (truncated)
            output += "\n[output truncated]";

        return new ProcessResult
        {
            ExitCode = process.ExitCode,
            Output = output,
            StandardError = ReadErrors()
        };

        string Read()
        {
            lock (captured)
                return captured.ToString();
        }

        string ReadErrors()
        {
            // Guarded by the same lock as the combined buffer, since both are
            // appended from the two reader callbacks.
            lock (captured)
                return errors.ToString();
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
