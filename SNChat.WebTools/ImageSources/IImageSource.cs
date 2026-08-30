namespace SNChat.WebTools.ImageSources;

/// <summary>A backend that can find pictures for a subject.</summary>
public interface IImageSource
{
    /// <summary>Name shown in settings and logs.</summary>
    string Name { get; }

    /// <summary>
    /// False when the source needs credentials that have not been configured.
    /// Callers use this to skip straight to a fallback.
    /// </summary>
    bool IsConfigured { get; }

    Task<IReadOnlyList<ImageResult>> SearchAsync(
        string query,
        int count,
        CancellationToken cancellationToken = default);
}

public class ImageResult
{
    public string Caption { get; init; } = string.Empty;

    /// <summary>Direct link to the image itself, ready to embed in markdown.</summary>
    public string ImageUrl { get; init; } = string.Empty;

    /// <summary>Page the image came from, shown as the attribution link.</summary>
    public string SourceUrl { get; init; } = string.Empty;
}
