using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SNChat.App.Converters;

/// <summary>
/// Shows an element only when the bound markdown contains a fenced code block,
/// so the "Copy code" action stays hidden on ordinary prose replies.
/// </summary>
public class HasCodeBlocksConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        MarkdownCode.HasBlocks(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
