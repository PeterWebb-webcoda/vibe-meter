namespace VibeMeter.Models;

/// <summary>Visual style used for the usage gauge display.</summary>
public enum MeterStyle
{
    Circular,
    Horizontal,
    Battery
}

public static class MeterStyleExtensions
{
    public static string GetTitle(this MeterStyle style) => style switch
    {
        MeterStyle.Circular   => "Circular",
        MeterStyle.Horizontal => "Horizontal",
        MeterStyle.Battery    => "Battery",
        _                     => style.ToString()
    };
}
