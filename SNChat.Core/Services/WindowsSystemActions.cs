using System.Text.RegularExpressions;

namespace SNChat.Core.Services;

/// <summary>
/// Turns windows-system-mcp's "Unknown action" refusal into something a model
/// can recover from.
///
/// The server packs 56 jobs into 7 tools chosen by an "action" argument, and
/// several actions read as though they belong to a different tool than they do:
/// get_top_processes lives on process_manager, get_top_processes_by_cpu lives on
/// performance. A model asked what is eating the CPU reaches for the obvious
/// combination and gets:
///
///   Unknown action: get_top_processes_by_cpu
///
/// which names neither what would have worked nor where the action it wanted
/// actually lives. So the model guesses again, usually wrongly, until it runs
/// out of tool iterations and tells the user the machine cannot be inspected.
///
/// Built from the tool list the server itself published at startup rather than
/// from a table written here, so it cannot drift out of step with the server.
/// </summary>
public sealed class WindowsSystemActions
{
    private static readonly Regex Unknown = new(
        @"Unknown action:\s*(?<action>[A-Za-z0-9_]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Dictionary<string, IReadOnlyList<string>> _byTool = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _owner = new(StringComparer.Ordinal);

    /// <summary>
    /// Records one tool's actions. Called once per windows-system-mcp tool as
    /// they are discovered.
    ///
    /// An action appearing on two tools keeps the first, which is arbitrary but
    /// harmless: the point is to name somewhere it does exist, and the valid
    /// list for the tool actually called is right there in the same message.
    /// </summary>
    public void Add(string toolName, IReadOnlyList<string> actions)
    {
        if (actions.Count == 0)
            return;

        _byTool[toolName] = actions;

        foreach (var action in actions)
            _owner.TryAdd(action, toolName);
    }

    /// <summary>True once any tool's actions are known.</summary>
    public bool IsEmpty => _byTool.Count == 0;

    /// <summary>
    /// Extra guidance to append to a failed call, or null when the failure was
    /// not a misnamed action.
    ///
    /// Deliberately narrow: only the server's own "Unknown action" wording is
    /// rewritten. A tool that failed for a real reason - no such process, access
    /// denied - must reach the model unchanged rather than dressed up as a
    /// naming mistake it can retry its way out of.
    /// </summary>
    public string? Explain(string toolName, string? toolOutput)
    {
        if (string.IsNullOrWhiteSpace(toolOutput))
            return null;

        var match = Unknown.Match(toolOutput);

        if (!match.Success)
            return null;

        var attempted = match.Groups["action"].Value;
        var message = $"'{attempted}' is not an action of the '{toolName}' tool.";

        if (_owner.TryGetValue(attempted, out var owner)
            && !string.Equals(owner, toolName, StringComparison.Ordinal))
        {
            message += $" It belongs to the '{owner}' tool - call that one instead, " +
                       $"with action '{attempted}'.";
        }

        if (_byTool.TryGetValue(toolName, out var actions))
            message += $" '{toolName}' accepts: {string.Join(", ", actions)}.";

        return message;
    }
}
