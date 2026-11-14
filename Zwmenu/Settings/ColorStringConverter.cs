using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Zwmenu.Settings;

public sealed class ColorStringConverter: IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        try
        {
            var color = (Color)System.Windows.Media.ColorConverter.ConvertFromString(value as string ?? "#FF3A3A3E")!;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
        {
            return new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E));
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
