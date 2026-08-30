using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SNChat.WebTools.ImageSources;

/// <summary>
/// Wikimedia Commons image search. Needs no credentials, so it doubles as the
/// fallback when a keyed source is unconfigured or over quota. Covers notable
/// subjects well; weak on stock, lifestyle, and commercial product imagery.
/// </summary>
public class CommonsImageSource : IImageSource
{
    // Commons serves the nearest available size at or above this width.
    private const int ThumbnailWidth = 360;

    private readonly HttpClient _httpClient;
    private readonly ILogger<CommonsImageSource> _logger;

    public string Name => "Wikimedia Commons";

    public bool IsConfigured => true;

    public CommonsImageSource(HttpClient httpClient, ILogger<CommonsImageSource> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        // Wikimedia's policy requires a descriptive User-Agent identifying the app.
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent",
                "SNChat/1.0 (local desktop chat client)");
        }
    }

    public async Task<IReadOnlyList<ImageResult>> SearchAsync(
        string query, int count, CancellationToken cancellationToken = default)
    {
        var url = "https://commons.wikimedia.org/w/api.php" +
                  "?action=query&generator=search&gsrnamespace=6" +
                  $"&gsrsearch={Uri.EscapeDataString(query)}" +
                  $"&gsrlimit={count}" +
                  "&prop=imageinfo&iiprop=url|extmetadata" +
                  $"&iiurlwidth={ThumbnailWidth}&format=json";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Commons returned {Status} for {Query}", response.StatusCode, query);
            return Array.Empty<ImageResult>();
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return Parse(json);
    }

    private List<ImageResult> Parse(string json)
    {
        var results = new List<ImageResult>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse the Commons response");
            return results;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("query", out var queryNode) ||
                !queryNode.TryGetProperty("pages", out var pages) ||
                pages.ValueKind != JsonValueKind.Object)
            {
                return results;
            }

            foreach (var page in pages.EnumerateObject())
            {
                if (!page.Value.TryGetProperty("imageinfo", out var infos) ||
                    infos.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var info in infos.EnumerateArray())
                {
                    // Prefer the scaled thumbnail; originals can be several
                    // thousand pixels wide.
                    var thumb = GetString(info, "thumburl") ?? GetString(info, "url");
                    if (thumb == null)
                        continue;

                    results.Add(new ImageResult
                    {
                        Caption = BuildCaption(page.Value),
                        ImageUrl = ImageUrl.ForMarkdown(thumb),
                        SourceUrl = GetString(info, "descriptionurl") ?? thumb
                    });
                    break;
                }
            }
        }

        return results;
    }

    /// <summary>Turns "File:Red_Panda.JPG" into a readable caption.</summary>
    private static string BuildCaption(JsonElement page)
    {
        var title = GetString(page, "title") ?? "Image";

        if (title.StartsWith("File:", StringComparison.OrdinalIgnoreCase))
            title = title[5..];

        var dot = title.LastIndexOf('.');
        if (dot > 0)
            title = title[..dot];

        return title.Replace('_', ' ').Trim();
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
