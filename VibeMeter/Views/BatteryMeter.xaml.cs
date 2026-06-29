using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeMeter.Models;

namespace VibeMeter.Views;

/// <summary>
/// Battery-shaped gauge that displays a <see cref="UsageGaugeData"/> percentage.
/// </summary>
public partial class BatteryMeter : UserControl
{
    public static readonly DependencyProperty GaugeProperty =
        DependencyProperty.Register(
            nameof(Gauge),
            typeof(UsageGaugeData),
            typeof(BatteryMeter),
            new PropertyMetadata(null, OnGaugeChanged));

    private const double FillMaxWidth = 112 - 10; // 102

    public BatteryMeter()
    {
        InitializeComponent();
    }

    public UsageGaugeData? Gauge
    {
        get => (UsageGaugeData?)GetValue(GaugeProperty);
        set => SetValue(GaugeProperty, value);
    }

    private static void OnGaugeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BatteryMeter meter)
        {
            meter.UpdateBattery();
        }
    }

    private void UpdateBattery()
    {
        var data = Gauge;
        if (data is null)
        {
            TitleText.Text = string.Empty;
            SubtitleText.Text = string.Empty;
            PercentText.Text = string.Empty;
            ResetTextBlock.Text = string.Empty;
            FillBorder.Width = 0;
            return;
        }

        TitleText.Text = data.Title;
        SubtitleText.Text = data.Subtitle;
        PercentText.Text = $"{data.ClampedPercent}%";
        ResetTextBlock.Text = data.ResetText;

        FillBorder.Width = FillMaxWidth * data.Ratio;
        FillBrush.Color = data.StatusBrush.Color;
    }
}
