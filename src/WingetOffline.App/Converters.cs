using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WingetOffline.UI;

/// <summary>null ← Collapsed ، غير null ← Visible (ConverterParameter=invert يعكس).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter as string == "invert";
        return ((value == null) ^ invert) ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
