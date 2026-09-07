namespace SNChat.Core.Services;

/// <summary>
/// How the assistant tells the loop it has finished.
///
/// A tool rather than a phrase in its prose, because the loop has to be able to
/// tell "I am done" from "I am describing being done" - and a model that writes
/// "the task is now complete" as part of a plan would otherwise stop the run
/// halfway. A tool call is unambiguous and shows up in the log.
///
/// Shared state rather than a return value because a tool receives only the
/// model's arguments and returns only text; there is no path from a tool call
/// back to whatever is driving the conversation.
/// </summary>
public class AgentSignals
{
    private readonly object _gate = new();

    private bool _complete;
    private string _summary = string.Empty;

    /// <summary>What the assistant said it had done, if anything.</summary>
    public string Summary
    {
        get { lock (_gate) return _summary; }
    }

    public void SignalComplete(string summary)
    {
        lock (_gate)
        {
            _complete = true;
            _summary = summary;
        }
    }

    /// <summary>
    /// Whether completion was signalled, clearing it as it reads. Consuming
    /// rather than peeking, so a signal from one run cannot silently stop the
    /// next one before it has done anything.
    /// </summary>
    public bool ConsumeComplete()
    {
        lock (_gate)
        {
            var complete = _complete;
            _complete = false;
            return complete;
        }
    }

    /// <summary>Discards anything left over, before a run starts.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _complete = false;
            _summary = string.Empty;
        }
    }
}
