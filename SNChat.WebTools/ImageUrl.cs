namespace SNChat.WebTools;

internal static class ImageUrl
{
    /// <summary>
    /// Prepares a URL for embedding in markdown that the WPF chat view renders.
    ///
    /// Markdig's WPF image renderer fails to load any URL whose path contains
    /// percent-escapes - it produces a zero-sized image with no error. This was
    /// verified to affect even plain ASCII escapes such as %2C for a comma, while
    /// the same URL with the path decoded loads correctly. Wikimedia returns
    /// heavily escaped paths for any filename with punctuation or non-ASCII
    /// characters, so those images silently failed to appear.
    ///
    /// Decoding the path avoids the bug, and the tracking query string is dropped
    /// because it adds noise to every caption without affecting the image.
    /// </summary>
    public static string ForMarkdown(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return url;

        // Drop the tracking query string, if present.
        var queryStart = url.IndexOf('?');
        if (queryStart >= 0)
            url = url[..queryStart];

        try
        {
            return Uri.UnescapeDataString(url);
        }
        catch (UriFormatException)
        {
            return url;
        }
    }
}
