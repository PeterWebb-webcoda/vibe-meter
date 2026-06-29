using System.Windows;
using System.Windows.Controls;
using VibeMeter.Models;

namespace VibeMeter.Views;

/// <summary>
/// Horizontal bar gauge that displays a <see cref="UsageGaugeData"/> percentage.
/// </summary>
public partial class BarMeter : UserControl
{
    public static readonly DependencyProperty GaugeProperty =
        DependencyProperty.Register(
            nameof(Gauge),
            typeof(UsageGaugeData),
            typeof(BarMeter),
            new PropertyMetadata(null, OnGaugeChanged));

    public BarMeter()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
    }

    public UsageGaugeData? Gauge
    {
        get => (UsageGaugeData?)GetValue(GaugeProperty);
        set => SetValue(GaugeProperty, value);
    }

    private static void OnGaugeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BarMeter meter)
        {
            meter.UpdateBar();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateBar();
    }

    private void UpdateBar()
    {
        var data = Gauge;
        if (data is null)
        {
            TitleText.Text = string.Empty;
            SubtitleText.Text = string.Empty;
            PercentText.Text = string.Empty;
            ResetTextBlock.Text = string.Empty;
            FillBar.Width = 0;
            return;
        }

        TitleText.Text = data.Title;
        SubtitleText.Text = data.Subtitle;
        PercentText.Text = $"{data.ClampedPercent}%";
        ResetTextBlock.Text = data.ResetText;

        var parentGrid = FillBar.Parent as Grid;
        double trackWidth = parentGrid?.ActualWidth ?? 0;
        FillBar.Width = trackWidth * data.Ratio;
        FillBar.Fill = data.StatusBrush;
    }
}
