namespace SNChat.Core.Services;

/// <summary>
/// Asks the user to agree to one act that changes the machine.
///
/// Separate from <see cref="IAccessPrompt"/> because the two questions are not
/// the same shape. Access is granted to a folder and then holds for the session;
/// this is a single irreversible thing done to a named target, and agreeing to
/// it once says nothing about the next one.
/// </summary>
public interface IActionPrompt
{
    /// <summary>
    /// Puts the question and waits. True means go ahead, this once.
    ///
    /// Implementations must be safe to call from a background thread, and must
    /// return false rather than throwing if they cannot ask.
    /// </summary>
    Task<bool> ConfirmAsync(
        string summary,
        string detail,
        string toolName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Says no to everything, without asking anyone. The default wherever there is
/// no user to ask - tests, and any path that runs without a window.
/// </summary>
public sealed class DenyAllActionPrompt : IActionPrompt
{
    public Task<bool> ConfirmAsync(
        string summary,
        string detail,
        string toolName,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
