using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using VibeMeter.Ui;

namespace VibeMeter.Views;

/// <summary>
/// Converts an <see cref="Rgb"/> to a <see cref="SolidColorBrush"/> for Fill /
/// Background bindings, using the same WPF adapter the code-behind uses.
/// </summary>
public class RgbToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Rgb rgb ? new SolidColorBrush(rgb.ToMediaColor()) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
