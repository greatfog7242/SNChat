using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>
/// Which project the conversation on screen is working in, shared with the
/// tools so they know which folder they may touch and how much they may do.
///
/// Held in one place rather than passed down because tools are constructed once
/// at startup and receive only the arguments the model supplies - there is no
/// path from a tool call back to the conversation that prompted it.
/// </summary>
public class ProjectContext
{
    /// <summary>
    /// The active project, or null for an ordinary chat with no project.
    ///
    /// Written from the UI thread and read from whatever thread a tool runs on.
    /// A reference assignment is atomic, and a tool reading the previous project
    /// for a moment after a switch is not a correctness problem: the guard still
    /// refuses anything outside a folder that was allowed at some point.
    /// </summary>
    public Project? Current { get; set; }

    /// <summary>
    /// The folders the build and run tools may work in: everything configured in
    /// Settings, plus the active project's own root.
    ///
    /// The project root is included because adding a project is itself the
    /// deliberate act of pointing the assistant at a folder - having to then
    /// list the same path again under Settings would be a step that teaches
    /// nothing and gets skipped.
    /// </summary>
    public IReadOnlyList<string> EffectiveRoots(BuildToolSettings settings)
    {
        var roots = new List<string>(settings.AllowedRoots);

        var project = Current;

        if (project != null && !string.IsNullOrWhiteSpace(project.RootPath))
            roots.Add(project.RootPath);

        return roots;
    }
}
