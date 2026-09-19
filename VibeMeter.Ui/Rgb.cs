namespace VibeMeter.Ui;

/// <summary>
/// Platform-neutral 24-bit colour, standing in for the WPF
/// <c>System.Windows.Media.Color</c> the UI logic used to carry around.
/// Host apps convert it to their own colour type at the rendering edge
/// (the WPF app via <c>ToMediaColor</c>).
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>Mirrors <c>Color.FromRgb</c> so call sites read as they always did.</summary>
    public static Rgb FromRgb(byte r, byte g, byte b) => new(r, g, b);
}
