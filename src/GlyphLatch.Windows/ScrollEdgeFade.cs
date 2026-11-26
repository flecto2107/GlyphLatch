using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GlyphLatch.Windows;

public sealed class ScrollEdgeFade : IMultiValueConverter
{
    public object? Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values is not [double height, double offset, double extent] || height <= 0 || extent <= 0)
            return null;

        bool above = offset > 0.01;
        bool below = extent - offset > 0.01;
        if (!above && !below) return null;

        double edge = Math.Min(18 / height, 0.24);
        var mask = new LinearGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, 0), EndPoint = new Point(0, height)
        };
        mask.GradientStops.Add(new GradientStop(above ? Colors.Transparent : Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, edge));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 1 - edge));
        mask.GradientStops.Add(new GradientStop(below ? Colors.Transparent : Colors.Black, 1));
        mask.Freeze();
        return mask;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
