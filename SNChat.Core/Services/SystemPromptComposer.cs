using SNChat.Core.Models;

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

    /// <summary>
    /// Names the project the conversation is working in, or empty when there is
    /// none.
    ///
    /// The tools already know the folder - they read it from
    /// <see cref="ProjectContext"/> - but the model does not, and picking a
    /// project in the toolbar is silent. Without this it either asks which
    /// folder is meant or calls list_projects to be told something the user has
    /// already said.
    ///
    /// One sentence on purpose. It goes into every request for the life of the
    /// conversation, so anything longer is a standing charge for a fact that is
    /// needed once.
    /// </summary>
    public static string DescribeProject(Project? project)
    {
        if (project == null || string.IsNullOrWhiteSpace(project.RootPath))
            return string.Empty;

        var name = string.IsNullOrWhiteSpace(project.Name)
            ? "this project"
            : $"the project \"{project.Name.Trim()}\"";

        // A project whose folder has been moved or deleted is worth saying out
        // loud: otherwise the model plans confidently against a path that will
        // fail on the first tool call.
        return project.RootExists
            ? $"You are working in {name}, whose folder is {project.RootPath}. "
              + "Unless the user names somewhere else, that folder is what a request refers to."
            : $"You are working in {name}, whose folder {project.RootPath} no longer exists, "
              + "so anything reaching into it will fail until the project is pointed elsewhere.";
    }
}
