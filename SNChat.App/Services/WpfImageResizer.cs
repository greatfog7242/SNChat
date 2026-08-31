using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using SNChat.Core.Interfaces;

namespace SNChat.App.Services;

/// <summary>
/// Downscales images using WPF's own imaging stack, which avoids taking a
/// third-party image library as a dependency.
/// </summary>
public class WpfImageResizer : IImageResizer
{
    private readonly ILogger<WpfImageResizer> _logger;

    public WpfImageResizer(ILogger<WpfImageResizer> logger)
    {
        _logger = logger;
    }

    public Task<string?> CreateDownscaledCopyAsync(
        string imagePath,
        int maxDimension,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var original = new BitmapImage();
            original.BeginInit();
            original.UriSource = new Uri(imagePath);
            original.CacheOption = BitmapCacheOption.OnLoad;
            original.EndInit();
            original.Freeze();

            var longest = Math.Max(original.PixelWidth, original.PixelHeight);
            if (longest <= maxDimension)
            {
                // Already small enough; sending the original avoids a needless
                // re-encode and the quality loss that comes with it.
                return Task.FromResult<string?>(null);
            }

            var scale = (double)maxDimension / longest;

            // DecodePixelWidth does the scaling during decode, so the full-size
            // bitmap is never materialised.
            var resized = new BitmapImage();
            resized.BeginInit();
            resized.UriSource = new Uri(imagePath);
            resized.CacheOption = BitmapCacheOption.OnLoad;
            resized.DecodePixelWidth = (int)Math.Round(original.PixelWidth * scale);
            resized.EndInit();
            resized.Freeze();

            // Encoder is chosen from the source, not fixed. Re-encoding a
            // photograph as lossless PNG inflates it badly - a 836 KB JPEG came
            // back as 1.33 MB, defeating the point. JPEG for photographic
            // sources, PNG for screenshots and diagrams where text must stay
            // legible and artefacts would be obvious.
            var sourceIsPhoto = Path.GetExtension(imagePath)
                .Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(imagePath)
                .Equals(".jpeg", StringComparison.OrdinalIgnoreCase);

            var targetPath = Path.Combine(
                Path.GetDirectoryName(imagePath)!,
                Path.GetFileNameWithoutExtension(imagePath) +
                (sourceIsPhoto ? ".model.jpg" : ".model.png"));

            BitmapEncoder encoder = sourceIsPhoto
                ? new JpegBitmapEncoder { QualityLevel = 85 }
                : new PngBitmapEncoder();

            encoder.Frames.Add(BitmapFrame.Create(resized));

            using (var stream = File.Create(targetPath))
                encoder.Save(stream);

            _logger.LogInformation(
                "Downscaled {Name} from {W}x{H} to {NewW}px for the model",
                Path.GetFileName(imagePath), original.PixelWidth, original.PixelHeight,
                resized.PixelWidth);

            return Task.FromResult<string?>(targetPath);
        }
        catch (Exception ex)
        {
            // A corrupt or unsupported image should fall back to sending the
            // original rather than failing the attachment outright.
            _logger.LogWarning(ex, "Could not downscale {Path}", imagePath);
            return Task.FromResult<string?>(null);
        }
    }
}
