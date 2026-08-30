using System.Text.Json;
using Microsoft.Extensions.Logging;
using SNChat.Core.Services;

namespace SNChat.WebTools.WebSources;

/// <summary>
/// General web search via Google Programmable Search (Custom Search JSON API).
///
/// Uses the same credentials as the image source, minus searchType=image. This
/// covers subjects that are indexed on the web but absent from encyclopedic
/// sources, which the DuckDuckGo Instant Answer and Wikipedia lookups cannot
/// return at all.
/// </summary>
public class GoogleWebSource
{
    private const int MaxAllowedByApi = 10;

    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private readonly ILogger<GoogleWebSource> _logger;

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

    public GoogleWebSource(
        HttpClient httpClient,
        SettingsService settingsService,
        ILogger<GoogleWebSource> logger)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WebResult>> SearchAsync(
        string query, int count, CancellationToken cancellationToken = default)
    {
        var settings = _settingsService.GetCachedSettings();
        var apiKey = settings.Providers.GoogleApiKey;
        var engineId = settings.Providers.GoogleSearchEngineId;

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(engineId))
        {
            _logger.LogInformation("Google web search skipped: credentials not configured");
            return Array.Empty<WebResult>();
        }

        var num = Math.Clamp(count, 1, MaxAllowedByApi);
        var safe = settings.Tools.SafeSearch ? "active" : "off";

        var url = "https://www.googleapis.com/customsearch/v1" +
                  $"?key={Uri.EscapeDataString(apiKey)}" +
                  $"&cx={Uri.EscapeDataString(engineId)}" +
                  $"&q={Uri.EscapeDataString(query)}" +
                  $"&safe={safe}" +
                  $"&num={num}";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // 403 commonly means the Custom Search API is not enabled on the
            // project rather than a bad key; 429 means the daily quota is gone.
            _logger.LogWarning("Google web search failed ({Status}): {Body}",
                response.StatusCode, Truncate(body, 300));
            return Array.Empty<WebResult>();
        }

        return Parse(body);
    }

    private List<WebResult> Parse(string json)
    {
        var results = new List<WebResult>();

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse the Google web response");
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

                results.Add(new WebResult
                {
                    Title = GetString(item, "title") ?? link,
                    Url = link,
                    Snippet = Collapse(GetString(item, "snippet") ?? string.Empty)
                });
            }
        }

        return results;
    }

    /// <summary>Google embeds newlines in snippets; flatten them for markdown.</summary>
    private static string Collapse(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                             .Select(l => l.Trim()));

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "...";

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

public class WebResult
{
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string Snippet { get; init; } = string.Empty;
}
