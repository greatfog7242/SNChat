using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>
/// Recognises the calls to windows-system-mcp that do something to the machine
/// rather than report on it, and says what has to be true before one may run.
///
/// The server bundles its work into seven tools that each take an "action", so
/// the dangerous calls are not separate tools that could be left unregistered -
/// "list the services" and "stop the print spooler" are the same tool with a
/// different string in one argument. Whatever guards this has to read the
/// arguments; there is no tool list to filter.
///
/// Keyed on tool and action names rather than on which server they came from, so
/// a windows-system-mcp configured by hand in <see cref="ToolSettings.McpServers"/>
/// is guarded exactly like one switched on in Settings.
///
/// The names here were read off the running server on 2026-09-25, not from its
/// documentation: the integration guide this was built from lists tools like
/// "process_list" and "registry_write" that the server does not have, and claims
/// 42 tools where it publishes 7. See SNChat.Tests/probe_windows_mcp.js.
/// </summary>
public static class WindowsSystemGuard
{
    public const string FilesystemTool = "filesystem";
    public const string ProcessTool = "process_manager";
    public const string ServiceTool = "service_manager";
    public const string NetworkTool = "network";

    /// <summary>
    /// Every tool windows-system-mcp publishes, as of the 2026-09-25 probe.
    /// Recognising one by name is how a server configured by hand gets the same
    /// treatment as one switched on in Settings.
    /// </summary>
    public static readonly IReadOnlyList<string> ToolNames = new[]
    {
        FilesystemTool, ProcessTool, "system_info", "registry", ServiceTool, NetworkTool, "performance"
    };

    public static bool IsWindowsSystemTool(string toolName) => ToolNames.Contains(toolName);

    /// <summary>
    /// Whether this tool has calls that need the user's agreement. The other
    /// three - system_info, registry, performance - only report.
    ///
    /// Narrower than <see cref="IsWindowsSystemTool"/>, and separate from it on
    /// purpose: all seven are wrapped, because a misnamed action needs
    /// explaining wherever it happens, but only these four can ever stop to ask.
    /// </summary>
    public static bool Guards(string toolName) =>
        toolName is FilesystemTool or ProcessTool or ServiceTool or NetworkTool;

    /// <summary>
    /// Which Settings switch has to be on for a call to be offered at all.
    /// <see cref="None"/> means no switch governs it and the user is simply
    /// asked.
    /// </summary>
    public enum Capability
    {
        None,
        ProcessControl,
        ServiceControl
    }

    /// <summary>
    /// A call that needs the user's agreement, and the words to ask for it.
    /// <paramref name="Detail"/> names the actual target, which is the whole
    /// point: "stop a service" is not a question anyone can answer, and
    /// "stop MSSQLSERVER" is.
    /// </summary>
    public sealed record Confirmable(Capability Capability, string Summary, string Detail);

    /// <summary>
    /// Whether this call changes something, and what to tell the user if so.
    /// Read-only calls - listing, reading, pinging, every registry action the
    /// server has - come back false and run untouched.
    /// </summary>
    public static bool NeedsConsent(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments,
        out Confirmable confirmable)
    {
        confirmable = null!;

        var action = Text(arguments, "action");

        if (action.Length == 0)
            return false;

        switch (toolName)
        {
            case ProcessTool when action == "kill_process":
                confirmable = new Confirmable(
                    Capability.ProcessControl,
                    "End a running program?",
                    $"Process: {Describe(arguments, "process_id", "process_name")}");
                return true;

            case ServiceTool when action is "stop_service" or "start_service" or "restart_service":
                var verb = action.Replace("_service", string.Empty);

                confirmable = new Confirmable(
                    Capability.ServiceControl,
                    $"{char.ToUpperInvariant(verb[0])}{verb[1..]} a Windows service?",
                    $"Service: {Describe(arguments, "service_name")}");
                return true;

            // Reaching a machine that is not this one. Harmless against
            // localhost and rude-to-illegal against anything else, and the
            // model cannot tell the difference from the question it was asked.
            case NetworkTool when action == "scan_open_ports" && !IsLocal(Text(arguments, "host")):
                confirmable = new Confirmable(
                    Capability.None,
                    "Scan another machine for open ports?",
                    $"Host: {Text(arguments, "host")}");
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether the Settings switch governing <paramref name="capability"/> is on,
    /// and what to tell the model when it is not.
    ///
    /// The refusal names the setting. A model told only "not allowed" tries the
    /// same call again in a slightly different shape; one told where the switch
    /// is reports that to the user and stops.
    /// </summary>
    public static bool IsPermitted(
        Capability capability,
        WindowsSystemSettings settings,
        out string refusal)
    {
        refusal = string.Empty;

        switch (capability)
        {
            case Capability.ProcessControl when !settings.AllowProcessControl:
                refusal = "Ending processes is turned off. The user can enable it under " +
                          "Settings - Windows system. Report that rather than trying again.";
                return false;

            case Capability.ServiceControl when !settings.AllowServiceControl:
                refusal = "Starting and stopping Windows services is turned off. The user can " +
                          "enable it under Settings - Windows system. Report that rather than " +
                          "trying again.";
                return false;

            default:
                return true;
        }
    }

    /// <summary>
    /// The path whose folder must be consented to before this call runs, or null
    /// when the call reads no file contents.
    ///
    /// Only read_file qualifies. The server can reach every file on the machine,
    /// with no allowed-directories list of its own, so reading contents goes
    /// through the same folder consent as any other file access. Listing a
    /// directory, sizing a file, and hunting for large files are left alone:
    /// they reveal names and sizes, which is what makes the tool useful for
    /// "what is filling my disk", and not what is in the files.
    /// </summary>
    public static string? FileToConsentTo(
        string toolName,
        IReadOnlyDictionary<string, object?> arguments)
    {
        if (toolName != FilesystemTool || Text(arguments, "action") != "read_file")
            return null;

        var path = Text(arguments, "path");

        return path.Length == 0 ? null : path;
    }

    /// <summary>
    /// Whether a host is this machine. Anything unrecognised is treated as
    /// remote, so a name that cannot be classified is asked about rather than
    /// assumed harmless.
    /// </summary>
    private static bool IsLocal(string host)
    {
        if (host.Length == 0)
            return true;

        var trimmed = host.Trim().Trim('[', ']');

        return trimmed is "localhost" or "127.0.0.1" or "::1" or "0.0.0.0"
            || trimmed.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first of <paramref name="keys"/> that was actually supplied. The
    /// server accepts a process by id or by name, and the dialog should show
    /// whichever the model used rather than a blank for the other.
    /// </summary>
    private static string Describe(
        IReadOnlyDictionary<string, object?> arguments,
        params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = Text(arguments, key);

            if (value.Length > 0)
                return value;
        }

        return "(not specified)";
    }

    /// <summary>
    /// Arguments arrive as loosely typed JSON, so an id may be a number and an
    /// action may be a JsonElement. Comparison is on the text either way.
    /// </summary>
    private static string Text(IReadOnlyDictionary<string, object?> arguments, string key) =>
        arguments.TryGetValue(key, out var value) && value != null
            ? value.ToString()?.Trim() ?? string.Empty
            : string.Empty;
}
