using System.Globalization;
using System.Windows.Data;

namespace SNChat.App.Converters;

/// <summary>Enables a control only when the bound value is set.</summary>
public class NotNullConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value != null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
