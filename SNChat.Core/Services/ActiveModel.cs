namespace SNChat.Core.Services;

/// <summary>
/// Which provider and model the conversation on screen is using.
///
/// Exists for the same reason <see cref="ProjectContext"/> does: a tool receives
/// only the arguments the model supplied, so there is no path from inside a tool
/// call back to the conversation that made it. A subagent has to run on
/// something, and "whatever the user is on" is the only sensible default.
///
/// Names rather than a provider instance, so this stays in Core alongside the
/// other shared context and providers are still resolved through the factory.
/// </summary>
public class ActiveModel
{
    /// <summary>
    /// Written from the UI thread when the user changes provider or model, read
    /// from whatever thread a tool runs on. Both are reference assignments,
    /// which are atomic; a subagent started at the instant of a switch running
    /// on the previous model is not worth locking for.
    /// </summary>
    public string ProviderName { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;
}
