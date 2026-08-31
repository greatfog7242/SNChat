namespace SNChat.Core.Interfaces;

/// <summary>
/// Produces a smaller copy of an image for sending to vision models. Full
/// resolution costs real time and tokens - a 1800px photo measured at 1.09 MB of
/// base64, 2420 prompt tokens, and a 53 second round trip - while adding little
/// the model can use.
///
/// Implemented outside Core because image decoding is platform-specific.
/// </summary>
public interface IImageResizer
{
    /// <summary>
    /// Writes a downscaled copy beside the original and returns its path, or
    /// null when the image is already small enough or cannot be processed. The
    /// original is never modified.
    /// </summary>
    Task<string?> CreateDownscaledCopyAsync(
        string imagePath,
        int maxDimension,
        CancellationToken cancellationToken = default);
}
