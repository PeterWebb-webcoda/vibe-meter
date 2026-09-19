using System.Collections.Generic;

namespace VibeMeter.Ui.Models;

/// <summary>
/// A named colour tint applied to the widget UI.
/// </summary>
public class WidgetTint
{
    public string Name { get; }
    public Rgb Primary { get; }
    public Rgb Secondary { get; }
    public Rgb Glow { get; }

    public WidgetTint(string name, Rgb primary, Rgb secondary, Rgb glow)
    {
        Name = name;
        Primary = primary;
        Secondary = secondary;
        Glow = glow;
    }

    /// <summary>Built-in tint presets.</summary>
    public static IReadOnlyList<WidgetTint> All { get; } = new List<WidgetTint>
    {
        new("Aurora",
            primary:   Rgb.FromRgb(64, 194, 232),
            secondary: Rgb.FromRgb(235, 97, 184),
            glow:      Rgb.FromRgb(100, 151, 255)),

        new("Moss",
            primary:   Rgb.FromRgb(110, 199, 133),
            secondary: Rgb.FromRgb(245, 191, 89),
            glow:      Rgb.FromRgb(135, 219, 184)),

        new("Cinder",
            primary:   Rgb.FromRgb(255, 107, 89),
            secondary: Rgb.FromRgb(242, 196, 122),
            glow:      Rgb.FromRgb(255, 135, 102))
    };
}
