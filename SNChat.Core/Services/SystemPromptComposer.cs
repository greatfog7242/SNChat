namespace SNChat.Core.Services;

/// <summary>
/// Assembles the instructions sent ahead of every conversation.
///
/// The order is deliberate and runs from most general to most specific, because
/// where two instructions disagree the later one is the one meant to win: the
/// user's standing rules, then the rules for this project, then the answering
/// mode, then whatever a template asked for.
///
/// An absent part contributes nothing at all rather than an empty line, so a
/// conversation with no rules and no template produces exactly what it did
/// before any of this existed.
/// </summary>
public static class SystemPromptComposer
{
    /// <summary>
    /// The parts joined by a blank line, skipping any that are missing or blank.
    /// Returns empty when everything is absent, which means no system message is
    /// sent at all rather than an empty one.
    /// </summary>
    public static string Compose(params string?[] parts) =>
        string.Join("\n\n", parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));
}
