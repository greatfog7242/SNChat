using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace SNChat.App.Converters;

/// <summary>
/// Loads a local file into a decoded, frozen bitmap for a WPF Image.
///
/// Returning the Uri and letting WPF convert it was enough to show the picture,
/// but the BitmapImage built that way keeps the default OnDemand cache option,
/// which holds the file open for as long as the image is on screen. Deleting a
/// conversation then failed on its own attachments - the folder cannot be
/// removed while this process still has a handle inside it, and the error came
/// back as the file "being used by another process", the process being us.
/// Reading through a stream that is disposed here leaves no handle behind.
/// </summary>
public class FilePathToImageConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path))
            return null;

        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;

            // OnLoad is what makes the disposal above safe: it decodes the whole
            // picture during EndInit, so nothing is read back from the stream
            // afterwards. With the default the bitmap would outlive its source.
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            // A missing or unreadable attachment leaves the bubble without a
            // picture, which is better than throwing out of a binding.
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
