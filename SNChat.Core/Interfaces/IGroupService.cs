using SNChat.Core.Models;

namespace SNChat.Core.Interfaces;

public interface IGroupService
{
    /// <summary>Groups in the order the user arranged them.</summary>
    Task<IReadOnlyList<ConversationGroup>> GetGroupsAsync();

    Task<ConversationGroup> CreateGroupAsync(string name);

    Task RenameGroupAsync(Guid groupId, string name);

    /// <summary>
    /// Drops the group. The conversations it held are untouched and reappear
    /// among the ungrouped ones.
    /// </summary>
    Task DeleteGroupAsync(Guid groupId);

    /// <summary>
    /// Files conversations under one group, taking them out of any other.
    /// </summary>
    Task AssignAsync(IEnumerable<Guid> conversationIds, Guid groupId);

    /// <summary>Takes conversations out of whatever group holds them.</summary>
    Task UnassignAsync(IEnumerable<Guid> conversationIds);

    Task SetExpandedAsync(Guid groupId, bool isExpanded);

    /// <summary>
    /// Forgets conversations that no longer exist on disk, so a group's count
    /// matches what it can actually show.
    /// </summary>
    Task PruneAsync(IEnumerable<Guid> existingConversationIds);
}
