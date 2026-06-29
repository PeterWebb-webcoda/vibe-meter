using System;
using System.Windows.Media;

namespace VibeMeter.Models;

/// <summary>
/// UI-facing gauge data consumed by the meter controls. Mapped from
/// <see cref="VibeMeter.Core.UsageGauge"/> by the view model layer.
/// </summary>
public class UsageGaugeData
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public int Percent { get; set; }
    public DateTime? ResetAt { get; set; }

    /// <summary>Percent clamped to the 0–100 range.</summary>
    public int ClampedPercent => Math.Max(0, Math.Min(100, Percent));

    /// <summary>Clamped percent expressed as a 0.0–1.0 ratio.</summary>
    public double Ratio => ClampedPercent / 100.0;

    /// <summary>
    /// Green (50–100 %), amber (20–49 %), red (0–19 %).
    /// </summary>
    public SolidColorBrush StatusBrush
    {
        get
        {
            var colour = ClampedPercent switch
            {
                >= 50 => Color.FromRgb(64, 200, 115),
                >= 20 => Color.FromRgb(245, 173, 56),
                _     => Color.FromRgb(245, 72, 64)
            };
            return new SolidColorBrush(colour);
        }
    }

    /// <summary>Human-readable reset description, e.g. "resets 2:30 PM".</summary>
    public string ResetText => ResetAt.HasValue
        ? $"resets {ResetAt.Value:h:mm tt}"
        : "current window";
}
