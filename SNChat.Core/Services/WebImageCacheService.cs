using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SNChat.Core.Interfaces;

namespace SNChat.Core.Services;

/// <summary>
/// Copies the pictures a reply links to into the conversation's own attachments
/// folder and rewrites the markdown to point at the local copy.
///
/// Search results link to third-party hosts that rotate, expire, or rate-limit
/// their URLs, so a conversation reopened weeks later would show broken images.
/// Downloading once at the end of the turn makes old conversations render offline
/// and keeps the pictures with the transcript they belong to.
/// </summary>
public partial class WebImageCacheService
{
    /// <summary>
    /// Guards against a redirect to an HTML error page or a multi-megabyte
    /// original being written into every conversation folder.
    /// </summary>
    private const long MaxImageBytes = 20 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly IStorageService _storageService;
    private readonly ILogger<WebImageCacheService> _logger;

    public WebImageCacheService(
        HttpClient httpClient,
        IStorageService storageService,
        ILogger<WebImageCacheService> logger)
    {
        _httpClient = httpClient;
        _storageService = storageService;
        _logger = logger;

        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            // Wikimedia rejects requests without one, and Google's thumbnail
            // hosts serve a placeholder instead of the picture.
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SNChat/1.0");
        }
    }

    /// <summary>
    /// Downloads every remote image the markdown references and returns the
    /// markdown with those links pointing at the local copies. A download that
    /// fails leaves its link untouched, so the picture still loads from the web
    /// rather than turning into a dead local path.
    /// </summary>
    public async Task<string> CacheImagesAsync(
        string markdown,
        Guid conversationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return markdown;

        var matches = MarkdownImage().Matches(markdown);
        if (matches.Count == 0)
            return markdown;

        var attachmentsDirectory = _storageService.GetAttachmentsDirectory(conversationId);

        // Several results routinely point at the same picture, and the same URL
        // can appear twice in one reply. Resolve each distinct URL once.
        var localByUrl = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in matches)
        {
            var url = match.Groups["url"].Value;

            if (!IsRemoteImage(url) || localByUrl.ContainsKey(url))
                continue;

            var local = await DownloadAsync(url, attachmentsDirectory, cancellationToken);
            if (local != null)
                localByUrl[url] = local;
        }

        if (localByUrl.Count == 0)
            return markdown;

        _logger.LogInformation("Cached {Count} image(s) into the conversation folder",
            localByUrl.Count);

        return MarkdownImage().Replace(markdown, match =>
        {
            var url = match.Groups["url"].Value;
            return localByUrl.TryGetValue(url, out var local)
                ? $"![{match.Groups["alt"].Value}]({ConversationPaths.ToDisplayUri(local)})"
                : match.Value;
        });
    }

    /// <summary>
    /// Fetches one image. Returns the path written, or null when the download
    /// failed or the response was not actually an image.
    /// </summary>
    private async Task<string?> DownloadAsync(
        string url, string attachmentsDirectory, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Could not fetch {Url}: {Status}", url, response.StatusCode);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != null &&
                !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                // A hotlink block or an expired URL typically answers 200 with an
                // HTML page, which would otherwise be saved as a broken ".jpg".
                _logger.LogWarning("{Url} returned {MediaType}, not an image", url, mediaType);
                return null;
            }

            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                _logger.LogWarning("{Url} is {Size} bytes, over the cache limit",
                    url, response.Content.Headers.ContentLength);
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            if (bytes.Length == 0 || bytes.Length > MaxImageBytes)
                return null;

            Directory.CreateDirectory(attachmentsDirectory);

            var path = Path.Combine(
                attachmentsDirectory, BuildFileName(url, mediaType));

            // The name is derived from the URL, so an identical hit from an
            // earlier turn is already on disk and need not be written again.
            if (!File.Exists(path))
                await File.WriteAllBytesAsync(path, bytes, cancellationToken);

            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not cache the image at {Url}", url);
            return null;
        }
    }

    private static bool IsRemoteImage(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>
    /// Names the file after a hash of its URL, which keeps the name short and
    /// filesystem-safe regardless of the original, and makes a repeat of the
    /// same picture resolve to the file already saved.
    /// </summary>
    private static string BuildFileName(string url, string? mediaType)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();

        return $"web-{hash}{ResolveExtension(url, mediaType)}";
    }

    private static string ResolveExtension(string url, string? mediaType)
    {
        var fromUrl = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant();
        if (fromUrl is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".svg")
            return fromUrl;

        return mediaType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/bmp" => ".bmp",
            "image/svg+xml" => ".svg",
            _ => ".jpg"
        };
    }

    /// <summary>
    /// Matches a markdown image, capturing its alt text and target. Targets
    /// containing brackets or a title are left alone rather than mangled.
    /// </summary>
    [GeneratedRegex(@"!\[(?<alt>[^\]]*)\]\((?<url>[^()\s]+)\)")]
    private static partial Regex MarkdownImage();
}
