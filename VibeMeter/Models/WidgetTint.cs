using System.Collections.Generic;
using System.Windows.Media;

namespace VibeMeter.Models;

/// <summary>
/// A named colour tint applied to the widget UI.
/// </summary>
public class WidgetTint
{
    public string Name { get; }
    public Color Primary { get; }
    public Color Secondary { get; }
    public Color Glow { get; }

    public WidgetTint(string name, Color primary, Color secondary, Color glow)
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
            primary:   Color.FromRgb(64, 194, 232),
            secondary: Color.FromRgb(235, 97, 184),
            glow:      Color.FromRgb(100, 151, 255)),

        new("Moss",
            primary:   Color.FromRgb(110, 199, 133),
            secondary: Color.FromRgb(245, 191, 89),
            glow:      Color.FromRgb(135, 219, 184)),

        new("Cinder",
            primary:   Color.FromRgb(255, 107, 89),
            secondary: Color.FromRgb(242, 196, 122),
            glow:      Color.FromRgb(255, 135, 102))
    };
}
