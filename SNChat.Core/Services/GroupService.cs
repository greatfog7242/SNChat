using System.Text.Json;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>
/// Keeps the conversation groups in one small file beside the settings.
/// The list is read once and then held, since every screen that wants it wants
/// it on each keystroke of a search.
/// </summary>
public class GroupService : IGroupService
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _groupsPath;
    private readonly string _corruptPath;

    /// <summary>
    /// Serializes the read-modify-write cycles. Every caller is on the UI
    /// thread, but each one awaits partway through, so two overlapping edits
    /// could otherwise both save and one would lose.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private List<ConversationGroup>? _groups;

    public GroupService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var configDir = Path.Combine(appData, "SNChat", "config");
        Directory.CreateDirectory(configDir);
        _groupsPath = Path.Combine(configDir, "groups.json");
        _corruptPath = Path.Combine(configDir, "groups.corrupt.json");
    }

    public async Task<IReadOnlyList<ConversationGroup>> GetGroupsAsync()
    {
        await _gate.WaitAsync();
        try
        {
            return (await LoadAsync()).Select(Copy).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConversationGroup> CreateGroupAsync(string name)
    {
        await _gate.WaitAsync();
        try
        {
            var groups = await LoadAsync();
            var group = new ConversationGroup { Name = name };
            groups.Add(group);
            await SaveAsync();
            return Copy(group);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RenameGroupAsync(Guid groupId, string name)
    {
        await _gate.WaitAsync();
        try
        {
            var groups = await LoadAsync();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group == null)
                return;

            group.Name = name;
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteGroupAsync(Guid groupId)
    {
        await _gate.WaitAsync();
        try
        {
            var groups = await LoadAsync();
            if (groups.RemoveAll(g => g.Id == groupId) > 0)
                await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AssignAsync(IEnumerable<Guid> conversationIds, Guid groupId)
    {
        var ids = conversationIds.ToList();
        if (ids.Count == 0)
            return;

        await _gate.WaitAsync();
        try
        {
            var groups = await LoadAsync();
            var target = groups.FirstOrDefault(g => g.Id == groupId);
            if (target == null)
                return;

            // Out of every group first, including the target: that enforces the
            // one-group rule and also makes re-dropping into the same group a
            // no-op rather than a way to list a conversation twice.
            foreach (var group in groups)
                group.ConversationIds.RemoveAll(ids.Contains);

            target.ConversationIds.AddRange(ids);
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UnassignAsync(IEnumerable<Guid> conversationIds)
    {
        var ids = conversationIds.ToHashSet();
        if (ids.Count == 0)
            return;

        await _gate.WaitAsync();
        try
        {
            var groups = await LoadAsync();
            var changed = false;

            foreach (var group in groups)
                changed |= group.ConversationIds.RemoveAll(ids.Contains) > 0;

            if (changed)
                await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetExpandedAsync(Guid groupId, bool isExpanded)
    {
        await _gate.WaitAsync();
        try
        {
            var groups = await LoadAsync();
            var group = groups.FirstOrDefault(g => g.Id == groupId);
            if (group == null || group.IsExpanded == isExpanded)
                return;

            group.IsExpanded = isExpanded;
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PruneAsync(IEnumerable<Guid> existingConversationIds)
    {
        var alive = existingConversationIds.ToHashSet();

        await _gate.WaitAsync();
        try
        {
            var groups = await LoadAsync();
            var changed = false;

            foreach (var group in groups)
                changed |= group.ConversationIds.RemoveAll(id => !alive.Contains(id)) > 0;

            if (changed)
                await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Call only while holding <see cref="_gate"/>.</summary>
    private async Task<List<ConversationGroup>> LoadAsync()
    {
        if (_groups != null)
            return _groups;

        if (!File.Exists(_groupsPath))
        {
            _groups = new List<ConversationGroup>();
            return _groups;
        }

        try
        {
            var json = await File.ReadAllTextAsync(_groupsPath);
            _groups = JsonSerializer.Deserialize<List<ConversationGroup>>(json, ReadOptions)
                      ?? new List<ConversationGroup>();
        }
        catch
        {
            // Starting empty would have the next edit overwrite whatever was
            // there, so the unreadable file is moved aside first and the user
            // still has their groups to recover by hand.
            TryPreserveCorruptFile();
            _groups = new List<ConversationGroup>();
        }

        return _groups;
    }

    /// <summary>Call only while holding <see cref="_gate"/>.</summary>
    private async Task SaveAsync()
    {
        var json = JsonSerializer.Serialize(_groups, WriteOptions);
        await File.WriteAllTextAsync(_groupsPath, json);
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            File.Move(_groupsPath, _corruptPath, overwrite: true);
        }
        catch
        {
            // Nothing useful to do: the groups are a convenience over the
            // conversations, and failing to file them away must not stop the
            // list from loading.
        }
    }

    private static ConversationGroup Copy(ConversationGroup group) => new()
    {
        Id = group.Id,
        Name = group.Name,
        IsExpanded = group.IsExpanded,
        ConversationIds = new List<Guid>(group.ConversationIds)
    };
}
