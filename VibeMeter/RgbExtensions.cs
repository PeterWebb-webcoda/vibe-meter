using System.Windows.Media;
using VibeMeter.Ui;

namespace VibeMeter;

/// <summary>
/// WPF adapter for the platform-neutral <see cref="Rgb"/> colour: converts it
/// to a real <see cref="Color"/> at the rendering edge (views, converters and
/// code-behind), so the shared UI logic never needs a Windows assembly.
/// </summary>
public static class RgbExtensions
{
    public static Color ToMediaColor(this Rgb rgb) => Color.FromRgb(rgb.R, rgb.G, rgb.B);
}
