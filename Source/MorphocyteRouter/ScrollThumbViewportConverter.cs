using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MorphocyteRouter;

// Track's thumb must have a usable hit target, not a minimum Height that
// overflows the rectangle Track actually assigned to it.
public sealed class ScrollThumbViewportConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length != 6 || values.Take(5).Any(value => value is not double)) return 0d;
        var viewport = (double)values[0];
        var range = (double)values[2] - (double)values[1];
        var length = values[5] is Orientation.Horizontal ? (double)values[4] : (double)values[3];
        if (!double.IsFinite(viewport) || !double.IsFinite(range) || !double.IsFinite(length)) return 0d;
        const double minimum = 28;
        if (range <= 0 || length <= minimum + 1) return Math.Max(0, viewport);
        return Math.Max(viewport, minimum * range / (length - minimum));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        targetTypes.Select(_ => DependencyProperty.UnsetValue).ToArray();
}
