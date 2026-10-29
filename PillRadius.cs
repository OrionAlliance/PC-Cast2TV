using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace PCCast2TV;

// A pill's rounded ends: half its height, capped at 26 (the corner every 52 px button has).
public sealed class PillRadius : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        new CornerRadius(value is double h && h > 0 ? Math.Min(26, h / 2) : 26);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
