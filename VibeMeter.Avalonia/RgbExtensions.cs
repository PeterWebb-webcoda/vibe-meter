using Avalonia.Media;
using VibeMeter.Ui;

namespace VibeMeter.Avalonia;

/// <summary>
/// Avalonia adapter for the platform-neutral <see cref="Rgb"/> colour: converts
/// it to a real <see cref="Color"/> at the rendering edge (views, converters
/// and code-behind), so the shared UI logic never needs an Avalonia assembly.
/// Mirrors <c>VibeMeter/RgbExtensions.cs</c> in the WPF app.
/// </summary>
public static class RgbExtensions
{
    public static Color ToAvaloniaColor(this Rgb rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);
}
