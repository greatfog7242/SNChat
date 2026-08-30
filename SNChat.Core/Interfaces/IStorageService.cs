using SNChat.Core.Models;

namespace SNChat.Core.Interfaces;

public interface IStorageService
{
    Task<Conversation?> LoadConversationAsync(Guid id);
    Task<Conversation?> LoadConversationFromFileAsync(string filePath);
    Task SaveConversationAsync(Conversation conversation);
    Task<List<string>> GetAllConversationFilesAsync();
    Task DeleteConversationAsync(Guid id);
    string GetConversationFilePath(Guid id);
    string GetConversationDirectory(Guid id, DateTime? timestamp = null);
    string GetAttachmentsDirectory(Guid conversationId);
}
