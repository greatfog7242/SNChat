namespace SNChat.Core.Services;

/// <summary>
/// Asks the user whether the assistant may read a folder it has been refused.
///
/// An interface so the decision can be made by a dialog in the app and by a
/// plain "no" in a test, and so nothing in Core has to know what a window is.
/// </summary>
public interface IAccessPrompt
{
    /// <summary>
    /// Puts the question to the user and waits for an answer. True grants access
    /// to <paramref name="folder"/> for the rest of the session.
    ///
    /// <paramref name="requestedPath"/> is the file the assistant actually asked
    /// for, which is usually more meaningful to the user than the folder being
    /// granted - and the two differ, which is exactly why both are shown.
    ///
    /// Implementations must be safe to call from a background thread, and must
    /// return false rather than throwing if they cannot ask.
    /// </summary>
    Task<bool> RequestAccessAsync(
        string folder,
        string requestedPath,
        string toolName,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Says no to everything, without asking anyone.
///
/// The default wherever there is no user to ask - tests, and any code path that
/// runs without a window. Refusing is the safe answer: the assistant carries on
/// with what it already had.
/// </summary>
public sealed class DenyAllAccessPrompt : IAccessPrompt
{
    public Task<bool> RequestAccessAsync(
        string folder,
        string requestedPath,
        string toolName,
        CancellationToken cancellationToken = default) => Task.FromResult(false);
}
