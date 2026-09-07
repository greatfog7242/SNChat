using System.Text;
using SNChat.Core.Tools;

namespace SNChat.BuildTools;

/// <summary>
/// Lets the assistant read this application's own log.
///
/// Worth having because a tool that refuses says why in the log and nowhere
/// else. When edit_file was failing on every single call, the only visible sign
/// was the model quietly giving up and rewriting whole files instead - the
/// reason was sitting in the log the whole time, and neither the model nor the
/// user could see it.
///
/// The only tool here that takes no path from the model: the folder is fixed at
/// construction, so there is no traversal surface at all.
/// </summary>
public class ReadAppLogTool : ITool
{
    private const int DefaultLines = 100;
    private const int MaxLines = 500;

    private readonly string _logDirectory;

    public string Name => "read_app_log";

    public string Description =>
        "Read the end of this application's own log. Use it to find out why one " +
        "of your own tool calls failed or was refused, since tools report their " +
        "reasons here. Not for reading a project's files - use the filesystem " +
        "tools for that.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["lines"] = new()
            {
                Type = "integer",
                Description = $"How many lines from the end to return (1-{MaxLines}). " +
                              $"Defaults to {DefaultLines}."
            },
            ["contains"] = new()
            {
                Type = "string",
                Description = "Return only lines containing this text, e.g. a tool name. " +
                              "The line count then applies to the matches."
            }
        }
    };

    public ReadAppLogTool(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var newest = NewestLog();

        if (newest == null)
            return Task.FromResult("There is no log file to read yet.");

        var lines = ReadLines(arguments, "lines", DefaultLines, 1, MaxLines);
        var contains = arguments.TryGetValue("contains", out var raw)
            ? raw?.ToString()?.Trim()
            : null;

        List<string> all;

        try
        {
            // Shared read: the logger has this file open for writing, so an
            // exclusive open would fail every time.
            using var stream = new FileStream(
                newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            all = new List<string>();

            while (reader.ReadLine() is { } line)
                all.Add(line);
        }
        catch (IOException ex)
        {
            return Task.FromResult($"Could not read the log: {ex.Message}");
        }

        var matched = string.IsNullOrEmpty(contains)
            ? all
            : all.Where(l => l.Contains(contains, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matched.Count == 0)
        {
            return Task.FromResult(string.IsNullOrEmpty(contains)
                ? "The log is empty."
                : $"Nothing in the log contains '{contains}'.");
        }

        var tail = matched.Skip(Math.Max(0, matched.Count - lines)).ToList();

        var report = new StringBuilder();
        report.AppendLine(
            $"Last {tail.Count} of {matched.Count} matching line(s) from {Path.GetFileName(newest)}:");
        report.AppendLine();

        foreach (var line in tail)
            report.AppendLine(line);

        return Task.FromResult(report.ToString().TrimEnd());
    }

    /// <summary>
    /// The log being written now. Serilog rolls by date and again by size, so
    /// the newest file is found by write time rather than by name - a name-sorted
    /// pick returns yesterday's file whenever a size roll has happened today.
    /// </summary>
    private string? NewestLog()
    {
        try
        {
            if (!Directory.Exists(_logDirectory))
                return null;

            return new DirectoryInfo(_logDirectory)
                .GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static int ReadLines(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        int fallback,
        int minimum,
        int maximum)
    {
        if (!arguments.TryGetValue(key, out var raw) || raw is null)
            return fallback;

        return int.TryParse(raw.ToString(), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }
}
