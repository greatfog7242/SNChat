using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.WebTools.WebSources;

namespace SNChat.WebTools;

/// <summary>
/// Web lookup backed by DuckDuckGo's official Instant Answer API
/// (https://api.duckduckgo.com). No API key is required.
///
/// This deliberately does NOT scrape html.duckduckgo.com. That endpoint serves
/// an image CAPTCHA ("cc=botnet") once an IP is flagged, and working around it
/// would mean defeating an anti-bot control that DuckDuckGo changes regularly.
/// The Instant Answer API is the supported interface and stays stable.
///
/// Coverage is narrower than a full search engine: entity, definition, and
/// factual queries return good abstracts, while live data (weather, prices,
/// news) and subjective queries ("best laptop") return nothing. When there is
/// no answer the tool says so explicitly, so the model reports the gap instead
/// of inventing one.
/// </summary>
public class WebSearchTool : ITool
{
    private const int MaxRelatedTopics = 5;
    private const int GoogleResultCount = 5;

    private readonly HttpClient _httpClient;
    private readonly GoogleWebSource _googleWeb;
    private readonly SettingsService _settingsService;
    private readonly ILogger<WebSearchTool> _logger;

    public string Name => "web_search";

    public string Description =>
        "Search the web for information about a topic, person, organisation, place, " +
        "or technical term. Sometimes returns a picture of the subject as well as " +
        "text. It cannot search for video. If it reports no results, say so rather " +
        "than guessing.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["query"] = new()
            {
                Type = "string",
                Description = "The subject to look up, as a bare noun phrase such as " +
                              "\"Ada Lovelace\" or \"Eiffel Tower\". Do NOT append words " +
                              "like \"image\", \"photo\", or \"pictures\", and do not phrase " +
                              "it as a question - doing so prevents a match. Any picture " +
                              "available is returned automatically."
            }
        },
        Required = new List<string> { "query" }
    };

    public WebSearchTool(
        HttpClient httpClient,
        GoogleWebSource googleWeb,
        SettingsService settingsService,
        ILogger<WebSearchTool> logger)
    {
        _httpClient = httpClient;
        _googleWeb = googleWeb;
        _settingsService = settingsService;
        _logger = logger;

        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "SNChat/1.0");
        }
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetValue("query", out var rawQuery) || rawQuery is null)
            return "Error: no 'query' argument was provided.";

        var query = NormaliseQuery(rawQuery.ToString());
        if (string.IsNullOrWhiteSpace(query))
            return "Error: the 'query' argument was empty.";

        _logger.LogInformation("Web search: {Query}", query);

        // Google indexes the general web, so try it first when it is available;
        // the encyclopedic sources below only know subjects that have an article.
        var toolSettings = _settingsService.GetCachedSettings().Tools;
        var wantsGoogle = toolSettings.WebSource is WebSourcePreference.Google
                                                 or WebSourcePreference.Auto;

        if (wantsGoogle && _googleWeb.IsConfigured)
        {
            var googleResults = await TryGoogleAsync(query, cancellationToken);
            if (googleResults != null)
                return googleResults;

            _logger.LogInformation("Google returned nothing for {Query}; using encyclopedic sources", query);
        }
        else if (toolSettings.WebSource == WebSourcePreference.Google)
        {
            _logger.LogWarning(
                "Google is selected but its API key or Search Engine ID is missing; using encyclopedic sources");
        }

        var url = "https://api.duckduckgo.com/" +
                  $"?q={Uri.EscapeDataString(query)}&format=json&no_html=1&skip_disambig=1";

        string json;
        try
        {
            // Note: this API answers with HTTP 202 even on success, so the status
            // code is not a reliable signal. Judge by the payload instead.
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            json = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Web search request failed for {Query}", query);
            return $"The search could not be completed: {ex.Message}";
        }

        var formatted = FormatAnswer(json, query);

        // DuckDuckGo supplies a picture for only a minority of subjects, so fall
        // back to Wikipedia, which has both wider image coverage and a summary
        // when DuckDuckGo had nothing at all.
        var needsImage = formatted == null || !formatted.Contains("![", StringComparison.Ordinal);
        if (needsImage)
        {
            var wiki = await TryWikipediaAsync(query, cancellationToken);
            if (wiki != null)
            {
                formatted = formatted == null
                    ? wiki.ToMarkdown()
                    : wiki.PrependImageTo(formatted);
            }
        }

        if (formatted == null)
        {
            _logger.LogInformation("No result available for {Query}", query);
            return $"No information was found for \"{query}\". These sources cover " +
                   "factual and definitional topics, not live data or subjective " +
                   "queries. Tell the user the lookup found nothing instead of " +
                   "guessing an answer.";
        }

        return formatted;
    }

    /// <summary>
    /// Runs the Google web search, returning formatted markdown, or null when it
    /// produced nothing so the caller can fall through to the other sources.
    /// </summary>
    private async Task<string?> TryGoogleAsync(string query, CancellationToken cancellationToken)
    {
        IReadOnlyList<WebResult> results;
        try
        {
            results = await _googleWeb.SearchAsync(query, GoogleResultCount, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Google web search failed for {Query}", query);
            return null;
        }

        if (results.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine($"Web results for \"{query}\" (via Google):");
        sb.AppendLine();

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.AppendLine($"{i + 1}. [{r.Title}]({r.Url})");
            if (!string.IsNullOrWhiteSpace(r.Snippet))
                sb.AppendLine($"   {r.Snippet}");
            sb.AppendLine();
        }

        sb.AppendLine("Summarise these for the user and cite the links you rely on.");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Models tend to tack "image" or "photo" onto a subject when the user asks
    /// to see something, which stops these entity APIs matching at all. Strip
    /// those modifiers and question phrasing back to the bare subject.
    /// </summary>
    private static string NormaliseQuery(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var query = raw.Trim().Trim('?', '.', '!');

        string[] leading = { "what is ", "what are ", "who is ", "who was ", "tell me about " };
        foreach (var prefix in leading)
        {
            if (query.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                query = query[prefix.Length..];
                break;
            }
        }

        string[] trailing = { " image", " images", " photo", " photos", " picture", " pictures" };
        foreach (var suffix in trailing)
        {
            if (query.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                query = query[..^suffix.Length];
                break;
            }
        }

        return query.Trim();
    }

    private static string? ToRenderableUrl(string? url) =>
        url == null ? null : ImageUrl.ForMarkdown(url);

    private async Task<WikipediaSummary?> TryWikipediaAsync(string query, CancellationToken cancellationToken)
    {
        // The REST summary endpoint resolves loose casing and redirects, so the
        // raw subject can be passed straight through.
        var url = "https://en.wikipedia.org/api/rest_v1/page/summary/" +
                  Uri.EscapeDataString(query.Replace(' ', '_'));

        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var title = GetString(root, "title");
            var extract = GetString(root, "extract");
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(extract))
                return null;

            return new WikipediaSummary
            {
                Title = title,
                Extract = extract,
                // Thumbnail first: the original can be several thousand pixels
                // wide, which overwhelms the chat bubble since Markdig's image
                // Button ignores size constraints from the view.
                ImageUrl = ToRenderableUrl(
                    GetNestedString(root, "thumbnail", "source")
                    ?? GetNestedString(root, "originalimage", "source")),
                PageUrl = GetNestedString(root, "content_urls", "desktop", "page")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            _logger.LogWarning(ex, "Wikipedia lookup failed for {Query}", query);
            return null;
        }
    }

    private class WikipediaSummary
    {
        public string Title { get; init; } = string.Empty;
        public string Extract { get; init; } = string.Empty;
        public string? ImageUrl { get; init; }
        public string? PageUrl { get; init; }

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## {Title}");
            if (ImageUrl != null)
                sb.AppendLine($"![{Title}]({ImageUrl})");
            sb.AppendLine(Extract);
            if (PageUrl != null)
                sb.AppendLine($"Source: [Wikipedia]({PageUrl})");
            return sb.ToString().Trim();
        }

        /// <summary>Adds just the picture to an answer that already has text.</summary>
        public string PrependImageTo(string existing)
        {
            if (ImageUrl == null)
                return existing;

            var image = $"![{Title}]({ImageUrl})";

            // Keep the image under the heading if the answer opens with one.
            var lines = existing.Split('\n');
            if (lines.Length > 0 && lines[0].StartsWith("##", StringComparison.Ordinal))
                return lines[0] + "\n" + image + "\n" + string.Join('\n', lines.Skip(1));

            return image + "\n" + existing;
        }
    }

    private string? FormatAnswer(string json, string query)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Could not parse the search response");
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var sb = new StringBuilder();

            // A direct computed answer, e.g. unit conversions.
            var answer = GetString(root, "Answer");
            if (!string.IsNullOrWhiteSpace(answer))
                sb.AppendLine($"Answer: {answer}");

            // The main encyclopaedic summary.
            var abstractText = GetString(root, "AbstractText");
            if (!string.IsNullOrWhiteSpace(abstractText))
            {
                var heading = GetString(root, "Heading");
                if (!string.IsNullOrWhiteSpace(heading))
                    sb.AppendLine($"## {heading}");

                // Emitted as a markdown image so the chat renderer displays it
                // inline. Only some entities carry one.
                var image = GetAbsoluteImageUrl(root);
                if (image != null)
                    sb.AppendLine($"![{heading ?? query}]({ImageUrl.ForMarkdown(image)})");

                sb.AppendLine(abstractText);

                var source = GetString(root, "AbstractSource") ?? "Source";
                var sourceUrl = GetString(root, "AbstractURL");
                if (!string.IsNullOrWhiteSpace(sourceUrl))
                    sb.AppendLine($"Source: [{source}]({sourceUrl})");
            }

            // Dictionary-style definition, present for term lookups.
            var definition = GetString(root, "Definition");
            if (!string.IsNullOrWhiteSpace(definition))
            {
                sb.AppendLine();
                sb.AppendLine($"Definition: {definition}");
                var defUrl = GetString(root, "DefinitionURL");
                var defSource = GetString(root, "DefinitionSource") ?? "Source";
                if (!string.IsNullOrWhiteSpace(defUrl))
                    sb.AppendLine($"Source: [{defSource}]({defUrl})");
            }

            AppendRelatedTopics(root, sb);

            var result = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
    }

    private static void AppendRelatedTopics(JsonElement root, StringBuilder sb)
    {
        if (!root.TryGetProperty("RelatedTopics", out var topics) ||
            topics.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var lines = new List<string>();

        foreach (var topic in topics.EnumerateArray())
        {
            if (lines.Count >= MaxRelatedTopics)
                break;

            // Entries are either a topic or a nested category containing topics.
            var text = GetString(topic, "Text");
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var url = GetString(topic, "FirstURL");
            lines.Add(string.IsNullOrWhiteSpace(url) ? $"- {text}" : $"- [{text}]({url})");
        }

        if (lines.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("Related:");
        foreach (var line in lines)
            sb.AppendLine(line);
    }

    /// <summary>
    /// The API returns image paths relative to duckduckgo.com (e.g. "/i/ab12.png"),
    /// which will not load without the host. Returns null when no image is offered
    /// or when it is only a site logo rather than a picture of the subject.
    /// </summary>
    private static string? GetAbsoluteImageUrl(JsonElement root)
    {
        var image = GetString(root, "Image");
        if (string.IsNullOrWhiteSpace(image))
            return null;

        if (root.TryGetProperty("ImageIsLogo", out var isLogo) &&
            isLogo.ValueKind == JsonValueKind.Number &&
            isLogo.GetInt32() == 1)
        {
            return null;
        }

        if (image.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            image.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return image;
        }

        return "https://duckduckgo.com" + (image.StartsWith('/') ? image : "/" + image);
    }

    /// <summary>Walks a chain of nested object properties, e.g. thumbnail.source.</summary>
    private static string? GetNestedString(JsonElement element, params string[] path)
    {
        var current = element;

        for (var i = 0; i < path.Length - 1; i++)
        {
            if (!current.TryGetProperty(path[i], out var next) ||
                next.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            current = next;
        }

        return GetString(current, path[^1]);
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
