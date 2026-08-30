using System.Text.Json;
using Microsoft.Extensions.Logging;
using SNChat.Core.Services;

namespace SNChat.WebTools.ImageSources;

/// <summary>
/// Google Programmable Search (Custom Search JSON API) with searchType=image.
///
/// Searches the whole web, so it covers the product photography, stock imagery,
/// and recent material that Wikimedia Commons lacks. Requires two credentials:
/// an API key and a Search Engine ID (cx). The search engine must additionally
/// be configured to "Search the entire web" and to allow image search, otherwise
/// it only looks at explicitly listed sites and returns almost nothing.
///
/// The free tier is limited per day; once exhausted the API replies 429 and the
/// caller falls back to Commons rather than failing the request.
/// </summary>
public class GoogleImageSource : IImageSource
{
    private const int MaxAllowedByApi = 10;

    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private readonly ILogger<GoogleImageSource> _logger;

    public string Name => "Google";

    public bool IsConfigured
    {
        get
        {
            var providers = _settingsService.GetCachedSettings().Providers;
            return !string.IsNullOrWhiteSpace(providers.GoogleApiKey) &&
                   !string.IsNullOrWhiteSpace(providers.GoogleSearchEngineId);
        }
    }

    public GoogleImageSource(
        HttpClient httpClient,
        SettingsService settingsService,
        ILogger<GoogleImageSource> logger)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ImageResult>> SearchAsync(
        string query, int count, CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetCachedSettings();
        var providers = settings.Providers;
        var apiKey = providers.GoogleApiKey;
        var engineId = providers.GoogleSearchEngineId;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(engineId))
        {
            _logger.LogInformation("Google image search skipped: credentials not configured");
            return Array.Empty<ImageResult>();
        }

        // The API rejects num values above 10.
        var num = Math.Clamp(count, 1, MaxAllowedByApi);

        var url = "https://www.googleapis.com/customsearch/v1" +
                  $"?key={Uri.EscapeDataString(apiKey)}" +
                  $"&cx={Uri.EscapeDataString(engineId)}" +
                  $"&q={Uri.EscapeDataString(query)}" +
                  "&searchType=image" +
                  $"&safe={(settings.Tools.SafeSearch ? "active" : "off")}" +
                  $"&num={num}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 403 usually means a bad key or the API not enabled; 429 means the
            // daily quota is gone. Both are worth surfacing distinctly in logs.
            _logger.LogWarning("Google image search failed ({Status}): {Body}",
                response.StatusCode, Truncate(body, 300));
            return Array.Empty<ImageResult>();
        }

        return Parse(body);
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
            _logger.LogWarning(ex, "Could not parse the Google response");
            return results;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return results;
            }

            foreach (var item in items.EnumerateArray())
            {
                var link = GetString(item, "link");
                if (string.IsNullOrWhiteSpace(link))
                    continue;

                // contextLink is the page hosting the image; fall back to the
                // image itself so the attribution link is never empty.
                var context = item.TryGetProperty("image", out var image)
                    ? GetString(image, "contextLink")
                    : null;

                results.Add(new ImageResult
                {
                    Caption = GetString(item, "title") ?? "Image",
                    ImageUrl = ImageUrl.ForMarkdown(link),
                    SourceUrl = context ?? link
                });
            }
        }

        return results;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
