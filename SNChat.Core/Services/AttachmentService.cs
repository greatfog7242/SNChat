using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Interfaces;
using SNChat.Core.Models;

namespace SNChat.Core.Services;

/// <summary>
/// Copies dropped files into the conversation's own attachments folder and, for
/// text-based formats, pulls out the contents so they can be given to the model.
/// </summary>
public class AttachmentService
{
    /// <summary>
    /// Files above this are rejected outright. Text this large would blow the
    /// context window long before it is a storage problem.
    /// </summary>
    public const long MaxFileSizeBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Extracted text is capped so one large file cannot crowd out the rest of
    /// the conversation. The truncation is stated in the text itself so the
    /// model knows it is not seeing the whole file.
    /// </summary>
    private const int MaxExtractedChars = 50_000;

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".js", ".ts", ".jsx", ".tsx", ".py", ".java", ".c", ".h", ".cpp", ".hpp",
        ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".scala", ".sh", ".ps1", ".sql",
        ".html", ".css", ".scss", ".xaml", ".xml", ".json", ".yaml", ".yml", ".toml"
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".log", ".rst", ".ini", ".cfg", ".env"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg"
    };

    /// <summary>
    /// Longest edge sent to vision models. Above this, encoding and prompt cost
    /// climb sharply for little gain.
    /// </summary>
    private const int MaxImageDimension = 1024;

    private readonly IStorageService _storageService;
    private readonly IImageResizer? _imageResizer;
    private readonly ILogger<AttachmentService> _logger;

    public AttachmentService(
        IStorageService storageService,
        ILogger<AttachmentService> logger,
        IImageResizer? imageResizer = null)
    {
        _storageService = storageService;
        _logger = logger;
        _imageResizer = imageResizer;
    }

    /// <summary>
    /// Copies the file next to its conversation and extracts text where possible.
    /// Returns null when the file is missing or too large.
    /// </summary>
    public async Task<Attachment?> AttachAsync(
        string sourcePath,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath))
        {
            _logger.LogWarning("Attachment source does not exist: {Path}", sourcePath);
            return null;
        }

        var info = new FileInfo(sourcePath);
        if (info.Length > MaxFileSizeBytes)
        {
            _logger.LogWarning("Attachment {Name} is {Size} bytes, over the limit",
                info.Name, info.Length);
            return null;
        }

        var directory = _storageService.GetAttachmentsDirectory(conversationId);
        Directory.CreateDirectory(directory);

        var targetPath = MakeUniquePath(directory, info.Name);

        try
        {
            File.Copy(sourcePath, targetPath, overwrite: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not copy attachment {Name}", info.Name);
            return null;
        }

        var extension = info.Extension;
        var attachment = new Attachment
        {
            FileName = info.Name,
            FilePath = targetPath,
            FileSize = info.Length,
            Type = ClassifyType(extension),
            MimeType = GuessMimeType(extension)
        };

        if (attachment.Type is AttachmentType.Code or AttachmentType.Document)
        {
            attachment.ExtractedText = await ExtractTextAsync(targetPath, cancellationToken);
        }
        else if (attachment.Type == AttachmentType.Image && _imageResizer != null &&
                 attachment.MimeType != "image/svg+xml")
        {
            attachment.ModelImagePath = await _imageResizer.CreateDownscaledCopyAsync(
                targetPath, MaxImageDimension, cancellationToken);
        }

        _logger.LogInformation("Attached {Name} ({Type}, {Size} bytes, text: {HasText})",
            attachment.FileName, attachment.Type, attachment.FileSize,
            attachment.ExtractedText != null);

        return attachment;
    }

    /// <summary>
    /// Renders attachments as text to prepend to the user's message. Images and
    /// unreadable formats are listed by name only, so the model can say it cannot
    /// read them instead of inventing contents.
    /// </summary>
    public static string BuildContextBlock(IReadOnlyList<Attachment> attachments)
    {
        if (attachments.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();

        foreach (var attachment in attachments)
        {
            if (!string.IsNullOrEmpty(attachment.ExtractedText))
            {
                var fence = attachment.Type == AttachmentType.Code
                    ? Path.GetExtension(attachment.FileName).TrimStart('.')
                    : string.Empty;

                sb.AppendLine($"--- Attached file: {attachment.FileName} ---");
                sb.AppendLine($"```{fence}");
                sb.AppendLine(attachment.ExtractedText);
                sb.AppendLine("```");
            }
            else if (attachment.Type == AttachmentType.Image)
            {
                // The image itself is sent alongside the message for vision
                // models, so this is only a label. It deliberately does not tell
                // the model it cannot see the picture.
                sb.AppendLine($"--- Attached image: {attachment.FileName} ---");
            }
            else
            {
                sb.AppendLine($"--- Attached file: {attachment.FileName} " +
                              $"({attachment.Type}, {FormatSize(attachment.FileSize)}) ---");
                sb.AppendLine("The contents of this file could not be read as text. " +
                              "Do not guess what it contains.");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes / (1024.0 * 1024.0):F1} MB"
    };

    public static AttachmentType ClassifyType(string extension)
    {
        if (CodeExtensions.Contains(extension)) return AttachmentType.Code;
        if (TextExtensions.Contains(extension)) return AttachmentType.Document;
        if (ImageExtensions.Contains(extension)) return AttachmentType.Image;
        return AttachmentType.Other;
    }

    private async Task<string?> ExtractTextAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken);

            if (text.Length > MaxExtractedChars)
            {
                text = text[..MaxExtractedChars] +
                       $"\n\n[File truncated at {MaxExtractedChars:N0} characters]";
            }

            return text;
        }
        catch (Exception ex)
        {
            // Binary content masquerading as a text extension lands here.
            _logger.LogWarning(ex, "Could not read {Path} as text", path);
            return null;
        }
    }

    /// <summary>Avoids clobbering an earlier attachment with the same name.</summary>
    private static string MakeUniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var i = 2; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }

    private static string GuessMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" or ".log" => "text/plain",
        ".md" or ".markdown" => "text/markdown",
        ".csv" => "text/csv",
        ".json" => "application/json",
        ".xml" or ".xaml" => "application/xml",
        ".html" => "text/html",
        ".css" or ".scss" => "text/css",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        _ => "application/octet-stream"
    };
}
