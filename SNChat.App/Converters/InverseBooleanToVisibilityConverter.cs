using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SNChat.App.Converters;

/// <summary>
/// Shows an element when a flag is false - the placeholder that stands in for
/// an editor while nothing is selected, and similar.
///
/// The built-in BooleanToVisibilityConverter only goes the other way, and
/// binding the same flag twice with opposite senses is common enough to be
/// worth one converter rather than a DataTrigger at every site.
/// </summary>
public class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Visibility.Collapsed or Visibility.Hidden;
}
