using System.Text;
using Microsoft.Extensions.Logging;
using SNChat.Core.Models;
using SNChat.Core.Services;
using SNChat.Core.Tools;
using SNChat.WebTools.ImageSources;

namespace SNChat.WebTools;

/// <summary>
/// Finds pictures for a subject. The model sees a single tool; which backend
/// actually runs is a user setting, so switching sources does not change what
/// the model has to reason about.
/// </summary>
public class ImageSearchTool : ITool
{
    private const int DefaultResults = 4;
    private const int MaxResults = 8;

    private readonly GoogleImageSource _google;
    private readonly CommonsImageSource _commons;
    private readonly SettingsService _settingsService;
    private readonly ILogger<ImageSearchTool> _logger;

    public string Name => "image_search";

    public string Description =>
        "Find pictures of a subject and show them to the user. Use this whenever " +
        "the user asks to see an image, photo, or picture of something. Returns " +
        "images with their source pages. Does not search for video.";

    public ToolParameterSchema Parameters => new()
    {
        Properties = new Dictionary<string, ToolParameterProperty>
        {
            ["query"] = new()
            {
                Type = "string",
                Description = "What to find pictures of, as a plain subject such as " +
                              "\"red panda\" or \"Eiffel Tower\". Do not include words " +
                              "like \"image\" or \"photo\" in the query itself."
            },
            ["count"] = new()
            {
                Type = "integer",
                Description = $"How many images to return (1-{MaxResults}). Defaults to {DefaultResults}."
            }
        },
        Required = new List<string> { "query" }
    };

    public ImageSearchTool(
        GoogleImageSource google,
        CommonsImageSource commons,
        SettingsService settingsService,
        ILogger<ImageSearchTool> logger)
    {
        _google = google;
        _commons = commons;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<string> ExecuteAsync(
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default)
    {
        if (!arguments.TryGetValue("query", out var rawQuery) || rawQuery is null)
            return "Error: no 'query' argument was provided.";

        var query = CleanQuery(rawQuery.ToString());
        if (string.IsNullOrWhiteSpace(query))
            return "Error: the 'query' argument was empty.";

        var count = ResolveCount(arguments);
        var toolSettings = _settingsService.GetCachedSettings().Tools;

        var (primary, fallback) = ResolveSources(toolSettings);

        _logger.LogInformation("Image search: {Query} (count {Count}, source {Source})",
            query, count, primary.Name);

        IReadOnlyList<ImageResult> results;
        try
        {
            results = await primary.SearchAsync(query, count, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Source} image search failed", primary.Name);
            results = Array.Empty<ImageResult>();
        }

        var usedSource = primary.Name;

        // Covers an exhausted daily quota, a bad key, or simply no matches.
        if (results.Count == 0 && fallback != null)
        {
            _logger.LogInformation("{Primary} returned nothing; falling back to {Fallback}",
                primary.Name, fallback.Name);
            try
            {
                results = await fallback.SearchAsync(query, count, cancellationToken);
                usedSource = fallback.Name;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Source} fallback failed", fallback.Name);
            }
        }

        if (results.Count == 0)
        {
            _logger.LogInformation("No images found for {Query}", query);
            return $"No images were found for \"{query}\". Tell the user nothing was " +
                   "found rather than describing an image you have not seen.";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Found {results.Count} image(s) for \"{query}\" via {usedSource}. " +
                      "Show these to the user using the markdown below verbatim, " +
                      "so the pictures render:");
        sb.AppendLine();

        foreach (var image in results)
        {
            sb.AppendLine($"![{image.Caption}]({image.ImageUrl})");
            sb.AppendLine($"*{image.Caption}* - [source]({image.SourceUrl})");
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Picks the backend to try first and what to fall back to. Google is only
    /// chosen when its credentials exist, so an unconfigured preference quietly
    /// behaves like Commons instead of failing every lookup.
    /// </summary>
    private (IImageSource Primary, IImageSource? Fallback) ResolveSources(ToolSettings settings)
    {
        var wantsGoogle = settings.ImageSource == ImageSourcePreference.Google ||
                          settings.ImageSource == ImageSourcePreference.Auto;

        if (wantsGoogle && _google.IsConfigured)
            return (_google, settings.FallbackToCommons ? _commons : null);

        if (settings.ImageSource == ImageSourcePreference.Google && !_google.IsConfigured)
        {
            _logger.LogWarning(
                "Google is selected but its API key or Search Engine ID is missing; using Commons");
        }

        return (_commons, null);
    }

    private static int ResolveCount(IReadOnlyDictionary<string, object?> arguments)
    {
        if (!arguments.TryGetValue("count", out var raw) || raw is null)
            return DefaultResults;

        // Models send this as a number or a string depending on the model.
        if (!int.TryParse(raw.ToString(), out var count))
            return DefaultResults;

        return Math.Clamp(count, 1, MaxResults);
    }

    /// <summary>
    /// Strips words models habitually append when a user asks to see something;
    /// they narrow the search index and cost real results.
    /// </summary>
    private static string CleanQuery(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var query = raw.Trim().Trim('?', '.', '!');

        string[] noise = { " image", " images", " photo", " photos", " picture", " pictures" };
        foreach (var suffix in noise)
        {
            if (query.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                query = query[..^suffix.Length];
                break;
            }
        }

        return query.Trim();
    }
}
