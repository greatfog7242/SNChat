namespace SNChat.Core.Models;

public enum MessageRole
{
    User,
    Assistant,
    System,

    /// <summary>
    /// A tool the assistant called and what it returned, kept as part of the
    /// conversation.
    ///
    /// These used to exist only inside a single request: each provider built the
    /// tool transcript in a local list and threw it away when the reply
    /// finished. That is survivable while a turn is one question and one answer,
    /// because the assistant summarises what it found in its prose. It is not
    /// survivable for a loop that carries on across turns, which would forget
    /// every file it read and every build it ran, and start each round guessing
    /// from its own summary.
    /// </summary>
    Tool
}
