using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.LLM.Interfaces;
using SNChat.LLM.Models;

namespace SNChat.LLM.Tools;

/// <summary>
/// Delegation: handing a piece of work to a named assistant that does it in a
/// conversation of its own and reports back.
///
/// The gain is context, not capability - a subagent can do nothing its parent
/// could not. What it changes is the cost. Reading nine files to find one
/// function puts nine file bodies in the conversation forever; sending an
/// explorer to do it puts back three sentences. On a long job that is the
/// difference between finishing and compacting away the findings that the work
/// was for.
/// </summary>
public class RunSubagentTool : ITool
{
    /// <summary>
    /// Tools a subagent never gets, whatever its definition asks for.
    ///
    /// <c>run_subagent</c> because a subagent that can delegate can delegate to
    /// itself, and nothing downstream would stop it.
    ///
    /// <c>task_complete</c> for a subtler reason worth stating plainly:
    /// <see cref="AgentSignals"/> is one shared object, so a subagent calling it
    /// would not be reporting its own completion - it would be telling the
    /// parent's autonomous loop that the whole run had finished. A delegated
    /// search would end the job it was helping with.
    /// </summary>
    public static readonly IReadOnlyList<string> NeverDelegated =
        new[] { "run_subagent", "task_complete" };

    /// <summary>
    /// How much of the subagent's answer comes back. Generous, because the
    /// answer is the entire product of the work, but bounded - the whole purpose
    /// is to not flood the parent, and a subagent that pastes a file back would
    /// undo that.
    /// </summary>
    public const int MaxReportCharacters = 6000;

    private readonly AgentDefinitionService _agents;

    /// <summary>
    /// Resolved on use rather than injected, to break a genuine cycle: this tool
    /// needs the registry to know which tools it may pass on, the registry is
    /// built by a factory that registers this tool, and the providers are built
    /// from the registry in turn. Taking them as functions means construction
    /// touches neither, and by the time one is called the registry is complete.
    /// </summary>
    private readonly Func<ILLMProviderFactory> _providers;
    private readonly Func<IToolRegistry> _registry;

    private readonly ActiveModel _activeModel;
    private readonly ILogger<RunSubagentTool> _logger;

    /// <summary>
    /// A snapshot, so that <see cref="Description"/> and <see cref="Parameters"/>
    /// stay pure property reads - they are evaluated every time a request is
    /// built, and touching the disk there would put file I/O on the path of
    /// every message the user sends.
    /// </summary>
    private volatile IReadOnlyList<AgentDefinition> _known = Array.Empty<AgentDefinition>();

    public string Name => "run_subagent";

    public string Description
    {
        get
        {
            var known = _known;

            if (known.Count == 0)
            {
                return "Delegate a task to a subagent. No subagents are defined yet, " +
                       "so there is nothing to delegate to.";
            }

            var text = new StringBuilder();

            text.Append(
                "Hand a self-contained piece of work to another assistant, which does it " +
                "in its own conversation and reports back a short answer. Use it when the " +
                "work would take several tool calls whose details you do not need to keep - " +
                "searching a codebase, or checking whether something builds. Give it the " +
                "whole task in one go: it cannot see this conversation and you cannot ask " +
                "it a follow-up. Available:");

            foreach (var agent in known)
                text.Append("\n- ").Append(agent.Name).Append(": ").Append(agent.Description);

            return text.ToString();
        }
    }

    public ToolParameterSchema Parameters
    {
        get
        {
            var known = _known;

            return new ToolParameterSchema
            {
                Properties = new Dictionary<string, ToolParameterProperty>
                {
                    ["agent"] = new()
                    {
                        Type = "string",
                        Description = "Which subagent to use.",
                        Enum = known.Count == 0
                            ? null
                            : known.Select(a => a.Name).ToList()
                    },
                    ["task"] = new()
                    {
                        Type = "string",
                        Description =
                            "What you want done, in full. State what you need to know or " +
                            "achieve and where to look, and say what a good answer contains. " +
                            "The subagent starts from nothing but this."
                    }
                },
                Required = new List<string> { "agent", "task" }
            };
        }
    }

    public RunSubagentTool(
        AgentDefinitionService agents,
        Func<ILLMProviderFactory> providers,
        Func<IToolRegistry> registry,
        ActiveModel activeModel,
        ILogger<RunSubagentTool> logger)
    {
        _agents = agents;
        _providers = providers;
        _registry = registry;
        _activeModel = activeModel;
        _logger = logger;

        // Loaded here so the description sent with the very first request names
        // the real agents. An empty snapshot would advertise having none.
        Refresh();
    }

    /// <summary>
    /// Re-reads the definitions from disk, so that editing an agent file takes
    /// effect without a restart.
    /// </summary>
    public void Refresh()
    {
        try
        {
            _known = _agents.LoadAll();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not reload subagent definitions");
        }
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        var requested = Text(arguments, "agent");
        var task = Text(arguments, "task");

        if (string.IsNullOrWhiteSpace(task))
            return "Error: a subagent needs a task describing what to do.";

        // Straight from disk rather than the snapshot, so a subagent added or
        // corrected during the session can be used immediately.
        Refresh();

        var known = _known;

        if (known.Count == 0)
            return "There are no subagents defined.";

        var agent = known.FirstOrDefault(
            a => string.Equals(a.Name, requested, StringComparison.OrdinalIgnoreCase));

        if (agent == null)
        {
            return $"There is no subagent named '{requested}'. Available: " +
                   string.Join(", ", known.Select(a => a.Name)) + ".";
        }

        var tools = ToolsFor(agent);

        // An agent that asked for tools and got none cannot do the job it was
        // written for - the usual cause is a definition naming an MCP tool from
        // a server that is not running. Better to say so than to spend a real
        // model call producing a confident answer from nothing.
        if (tools.Count == 0 && agent.AllowedTools.Count > 0)
        {
            _logger.LogWarning(
                "Subagent {Agent} has none of the tools it asks for: {Wanted}",
                agent.Name, string.Join(", ", agent.AllowedTools));

            return $"The {agent.Name} subagent needs " +
                   string.Join(", ", agent.AllowedTools) +
                   ", none of which are available, so it cannot do anything. Do the work yourself.";
        }

        var providerName = _activeModel.ProviderName;
        var model = string.IsNullOrWhiteSpace(agent.Model) ? _activeModel.Model : agent.Model;

        if (string.IsNullOrWhiteSpace(providerName) || string.IsNullOrWhiteSpace(model))
            return "No model is selected, so there is nothing to run the subagent on.";

        ILLMProvider provider;

        try
        {
            provider = _providers().GetProvider(providerName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not resolve provider {Provider} for a subagent", providerName);
            return $"Could not start the subagent: no provider named '{providerName}'.";
        }

        _logger.LogInformation(
            "Subagent {Agent} starting on {Provider}/{Model} with {ToolCount} tool(s)",
            agent.Name, providerName, model, tools.Count);

        var request = new GenerateRequest
        {
            Model = model,
            SystemPrompt = agent.SystemPrompt,
            Messages = new List<Message>
            {
                new() { Role = MessageRole.User, Content = task }
            },
            Tools = tools,
            MaxToolIterations = Math.Clamp(agent.MaxToolIterations, 1, 50),
            CancellationToken = cancellationToken
        };

        var answer = new StringBuilder();

        try
        {
            await foreach (var chunk in provider.GenerateStreamAsync(request))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Progress notices belong to the subagent's own run, not its
                // report.
                if (!chunk.IsStatus)
                    answer.Append(chunk.Content);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Returned rather than thrown: the parent can say what failed and
            // carry on, which is better than losing its own turn to this.
            _logger.LogError(ex, "Subagent {Agent} failed", agent.Name);
            return $"The {agent.Name} subagent failed: {ex.Message}";
        }

        var report = answer.ToString().Trim();

        if (report.Length == 0)
        {
            _logger.LogWarning("Subagent {Agent} returned nothing", agent.Name);
            return $"The {agent.Name} subagent finished without reporting anything.";
        }

        _logger.LogInformation(
            "Subagent {Agent} reported {Length} characters", agent.Name, report.Length);

        if (report.Length > MaxReportCharacters)
        {
            report = report[..MaxReportCharacters] +
                     $"\n\n[The {agent.Name} subagent's report was cut off here. Ask it " +
                     "again for a narrower piece if you need the rest.]";
        }

        return $"The {agent.Name} subagent reports:\n\n{report}";
    }

    /// <summary>
    /// The tools this agent may actually use: what it asked for, narrowed to
    /// what exists, minus the ones that are never delegated. An empty list in
    /// the definition means everything, which is convenient but worth narrowing
    /// - a smaller tool set is both a smaller prompt and a smaller blast radius.
    /// </summary>
    public IReadOnlyList<ITool> ToolsFor(AgentDefinition agent)
    {
        var available = _registry().GetTools()
            .Where(tool => !NeverDelegated.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (agent.AllowedTools.Count == 0)
            return available;

        var wanted = new HashSet<string>(agent.AllowedTools, StringComparer.OrdinalIgnoreCase);

        var granted = available.Where(tool => wanted.Contains(tool.Name)).ToList();

        // A definition naming a tool that is not registered would otherwise
        // leave the agent quietly unable to do the job it was written for, and
        // the only symptom would be a poor answer.
        var missing = agent.AllowedTools
            .Where(name => !granted.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "Subagent {Agent} asks for {Missing}, which {Reason}",
                agent.Name,
                string.Join(", ", missing),
                missing.Any(m => NeverDelegated.Contains(m, StringComparer.OrdinalIgnoreCase))
                    ? "is either unavailable or never delegated"
                    : "is not registered");
        }

        return granted;
    }

    private static string Text(IReadOnlyDictionary<string, object?> arguments, string key) =>
        arguments.TryGetValue(key, out var value) ? value?.ToString()?.Trim() ?? string.Empty : string.Empty;
}
