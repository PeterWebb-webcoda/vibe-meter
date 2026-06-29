using System;
using System.Globalization;
using System.Windows.Data;

namespace VibeMeter.Views;

/// <summary>
/// Converts a 0–100 percent value to a pixel width within a given track width.
/// Pass the track width as the converter parameter (e.g. "52").
/// </summary>
public class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int percent && parameter is string trackStr && double.TryParse(trackStr, out var track))
        {
            return Math.Max(0, Math.Min(track, track * Math.Clamp(percent, 0, 100) / 100.0));
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
