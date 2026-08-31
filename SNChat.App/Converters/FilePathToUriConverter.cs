using System.Globalization;
using System.Windows.Data;

namespace SNChat.App.Converters;

/// <summary>Converts a local file path to a URI that WPF Image can load.</summary>
public class FilePathToUriConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string path && !string.IsNullOrEmpty(path))
        {
            try
            {
                return new Uri(path, UriKind.Absolute);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
