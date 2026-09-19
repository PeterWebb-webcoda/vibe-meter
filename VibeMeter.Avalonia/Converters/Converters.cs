using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using VibeMeter.Ui;
using VibeMeter.Ui.Models;

namespace VibeMeter.Avalonia.Converters;

/// <summary>
/// Converts an <see cref="Rgb"/> to a <see cref="SolidColorBrush"/> for Fill /
/// Background / Foreground bindings, mirroring
/// <c>VibeMeter/Views/RgbToBrushConverter.cs</c> in the WPF app.
/// </summary>
public class RgbToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Rgb rgb ? new SolidColorBrush(rgb.ToAvaloniaColor()) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// True when the bound string is non-empty — the Avalonia stand-in for the
/// WPF "hide when null/empty" DataTriggers (PlanLabel, ResetNote, ...).
/// </summary>
public class NonEmptyToBoolConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Tint index → the tint's primary colour as a brush. Bound to
/// <see cref="ViewModels.MainViewModel.TintIndex"/> (not the computed
/// TintPrimary properties) so a tint changed from the Settings window — which
/// can only raise PropertyChanged for TintIndex — still updates the footer.
/// </summary>
public class TintIndexToBrushConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i
            ? new SolidColorBrush(WidgetTint.All[i % WidgetTint.All.Count].Primary.ToAvaloniaColor())
            : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Tint index → the tint's display name.</summary>
public class TintIndexToNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i ? WidgetTint.All[i % WidgetTint.All.Count].Name : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
