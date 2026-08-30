using System.Text;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;

namespace SNChat.Core.Services;

public class StorageService : IStorageService
{
    private readonly string _baseDirectory;
    private readonly ISerializer _yamlSerializer;
    private readonly IDeserializer _yamlDeserializer;

    public StorageService()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _baseDirectory = Path.Combine(appData, "SNChat");

        // Ensure directories exist
        Directory.CreateDirectory(Path.Combine(_baseDirectory, "conversations"));
        Directory.CreateDirectory(Path.Combine(_baseDirectory, "templates"));
        Directory.CreateDirectory(Path.Combine(_baseDirectory, "attachments"));

        _yamlSerializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();

        _yamlDeserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .Build();
    }

    public string GetConversationDirectory(Guid id, DateTime? timestamp = null)
    {
        var date = timestamp ?? DateTime.UtcNow;
        var monthDir = Path.Combine(_baseDirectory, "conversations", $"{date.Year}-{date.Month:D2}");
        var conversationDir = Path.Combine(monthDir, id.ToString());
        return conversationDir;
    }

    public string GetConversationFilePath(Guid id)
    {
        var conversationDir = GetConversationDirectory(id);
        Directory.CreateDirectory(conversationDir);
        Directory.CreateDirectory(Path.Combine(conversationDir, "attachments"));
        return Path.Combine(conversationDir, "conversation.md");
    }

    public async Task SaveConversationAsync(Conversation conversation)
    {
        conversation.UpdatedAt = DateTime.UtcNow;
        conversation.FilePath = GetConversationFilePath(conversation.Id);

        var markdown = GenerateMarkdown(conversation);
        await File.WriteAllTextAsync(conversation.FilePath, markdown);
    }

    public async Task<Conversation?> LoadConversationAsync(Guid id)
    {
        // Try to find the conversation directory
        var conversationsDir = Path.Combine(_baseDirectory, "conversations");
        if (!Directory.Exists(conversationsDir))
            return null;

        // Search through all month directories for the conversation
        var monthDirs = Directory.GetDirectories(conversationsDir, "*", SearchOption.TopDirectoryOnly);
        foreach (var monthDir in monthDirs)
        {
            var conversationDir = Path.Combine(monthDir, id.ToString());
            var filePath = Path.Combine(conversationDir, "conversation.md");

            if (File.Exists(filePath))
            {
                return await LoadConversationFromFileAsync(filePath);
            }
        }

        return null;
    }

    public async Task<Conversation?> LoadConversationFromFileAsync(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        var content = await File.ReadAllTextAsync(filePath);
        return ParseMarkdown(content, filePath);
    }

    public async Task<List<string>> GetAllConversationFilesAsync()
    {
        var conversationsDir = Path.Combine(_baseDirectory, "conversations");
        if (!Directory.Exists(conversationsDir))
            return new List<string>();

        var conversationFiles = new List<string>();
        var monthDirs = Directory.GetDirectories(conversationsDir, "*", SearchOption.TopDirectoryOnly);

        foreach (var monthDir in monthDirs)
        {
            var conversationDirs = Directory.GetDirectories(monthDir, "*", SearchOption.TopDirectoryOnly);
            foreach (var convDir in conversationDirs)
            {
                var filePath = Path.Combine(convDir, "conversation.md");
                if (File.Exists(filePath))
                {
                    conversationFiles.Add(filePath);
                }
            }
        }

        return conversationFiles;
    }

    public async Task DeleteConversationAsync(Guid id)
    {
        var conversationsDir = Path.Combine(_baseDirectory, "conversations");
        if (!Directory.Exists(conversationsDir))
            return;

        var monthDirs = Directory.GetDirectories(conversationsDir, "*", SearchOption.TopDirectoryOnly);
        foreach (var monthDir in monthDirs)
        {
            var conversationDir = Path.Combine(monthDir, id.ToString());
            if (Directory.Exists(conversationDir))
            {
                Directory.Delete(conversationDir, recursive: true);
                return;
            }
        }
    }

    public string GetAttachmentsDirectory(Guid conversationId)
    {
        var conversationDir = GetConversationDirectory(conversationId);
        return Path.Combine(conversationDir, "attachments");
    }

    private string GenerateMarkdown(Conversation conversation)
    {
        var sb = new StringBuilder();

        // YAML frontmatter
        sb.AppendLine("---");

        var frontmatter = new
        {
            id = conversation.Id,
            title = conversation.Title,
            created = conversation.CreatedAt.ToString("o"),
            updated = conversation.UpdatedAt.ToString("o"),
            model = conversation.Metadata.ModelName,
            provider = conversation.Metadata.Provider,
            parent_branch = conversation.ParentBranchId,
            branch_point = conversation.BranchPoint,
            tags = conversation.Metadata.Tags,
            parameters = new
            {
                temperature = conversation.Metadata.Parameters.Temperature,
                max_tokens = conversation.Metadata.Parameters.MaxTokens,
                top_p = conversation.Metadata.Parameters.TopP
            }
        };

        sb.AppendLine(_yamlSerializer.Serialize(frontmatter).TrimEnd());
        sb.AppendLine("---");
        sb.AppendLine();

        // Title
        sb.AppendLine($"# {conversation.Title}");
        sb.AppendLine();

        // Messages
        for (int i = 0; i < conversation.Messages.Count; i++)
        {
            var message = conversation.Messages[i];
            var timestamp = message.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");

            sb.AppendLine($"## Message {i + 1} ({message.Role}) - {timestamp}");
            sb.AppendLine(message.Content);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private Conversation ParseMarkdown(string content, string filePath)
    {
        // Split frontmatter and body
        var parts = content.Split(new[] { "---" }, StringSplitOptions.None);

        if (parts.Length < 3)
            throw new FormatException("Invalid markdown format: missing frontmatter");

        var frontmatterYaml = parts[1].Trim();
        var body = string.Join("---", parts.Skip(2)).Trim();

        // Parse frontmatter
        var frontmatter = _yamlDeserializer.Deserialize<Dictionary<string, object>>(frontmatterYaml);

        var conversation = new Conversation
        {
            Id = Guid.Parse(frontmatter["id"].ToString()!),
            Title = frontmatter["title"].ToString()!,
            CreatedAt = DateTime.Parse(frontmatter["created"].ToString()!),
            UpdatedAt = DateTime.Parse(frontmatter["updated"].ToString()!),
            FilePath = filePath
        };

        // Parse metadata
        if (frontmatter.ContainsKey("model"))
            conversation.Metadata.ModelName = frontmatter["model"].ToString()!;

        if (frontmatter.ContainsKey("provider"))
            conversation.Metadata.Provider = frontmatter["provider"].ToString()!;

        if (frontmatter.ContainsKey("parent_branch") && frontmatter["parent_branch"] != null)
        {
            var parentBranchStr = frontmatter["parent_branch"].ToString();
            if (!string.IsNullOrEmpty(parentBranchStr))
                conversation.ParentBranchId = Guid.Parse(parentBranchStr);
        }

        if (frontmatter.ContainsKey("branch_point"))
            conversation.BranchPoint = Convert.ToInt32(frontmatter["branch_point"]);

        if (frontmatter.ContainsKey("tags") && frontmatter["tags"] is List<object> tags)
            conversation.Metadata.Tags = tags.Select(t => t.ToString()!).ToList();

        // Parse parameters
        if (frontmatter.ContainsKey("parameters") && frontmatter["parameters"] is Dictionary<object, object> paramsDict)
        {
            if (paramsDict.ContainsKey("temperature"))
                conversation.Metadata.Parameters.Temperature = Convert.ToDouble(paramsDict["temperature"]);

            if (paramsDict.ContainsKey("max_tokens"))
                conversation.Metadata.Parameters.MaxTokens = Convert.ToInt32(paramsDict["max_tokens"]);

            if (paramsDict.ContainsKey("top_p"))
                conversation.Metadata.Parameters.TopP = Convert.ToDouble(paramsDict["top_p"]);
        }

        // Parse messages from markdown body
        var messageBlocks = body.Split(new[] { "## Message " }, StringSplitOptions.RemoveEmptyEntries);

        for (int i = 0; i < messageBlocks.Length; i++)
        {
            var block = messageBlocks[i].Trim();
            if (string.IsNullOrWhiteSpace(block))
                continue;

            // Parse header: "1 (User) - 2026-08-30 10:30:15"
            var lines = block.Split('\n', 2);
            if (lines.Length < 2)
                continue;

            var header = lines[0];
            var messageContent = lines[1].Trim();

            // Extract role from header
            var roleMatch = System.Text.RegularExpressions.Regex.Match(header, @"\((\w+)\)");
            if (!roleMatch.Success)
                continue;

            var roleStr = roleMatch.Groups[1].Value;
            if (!Enum.TryParse<MessageRole>(roleStr, true, out var role))
                continue;

            // Extract timestamp
            var timestampMatch = System.Text.RegularExpressions.Regex.Match(header, @"- (.+)$");
            var timestamp = timestampMatch.Success
                ? DateTime.Parse(timestampMatch.Groups[1].Value)
                : DateTime.UtcNow;

            conversation.Messages.Add(new Message
            {
                Role = role,
                Content = messageContent,
                Timestamp = timestamp,
                Index = i
            });
        }

        return conversation;
    }
}
