using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VibeMeter.Models;

namespace VibeMeter.Views;

/// <summary>
/// Circular arc gauge that displays a <see cref="UsageGaugeData"/> percentage.
/// </summary>
public partial class CircularMeter : UserControl
{
    public static readonly DependencyProperty GaugeProperty =
        DependencyProperty.Register(
            nameof(Gauge),
            typeof(UsageGaugeData),
            typeof(CircularMeter),
            new PropertyMetadata(null, OnGaugeChanged));

    public CircularMeter()
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
        if (d is CircularMeter meter)
        {
            meter.UpdateArc();
        }
    }

    private void UpdateArc()
    {
        var data = Gauge;
        if (data is null)
        {
            ArcPath.Data = null;
            PercentText.Text = "–";
            TitleText.Text = string.Empty;
            SubtitleText.Text = string.Empty;
            ResetTextBlock.Text = string.Empty;
            return;
        }

        PercentText.Text = data.ClampedPercent.ToString();
        TitleText.Text = data.Title;
        SubtitleText.Text = data.Subtitle;
        ResetTextBlock.Text = data.ResetText;

        const double centreX = 35;
        const double centreY = 35;
        const double radius = 31;
        const double startAngle = -90.0; // 12 o'clock

        int pct = data.ClampedPercent;
        if (pct <= 0)
        {
            ArcPath.Data = null;
            return;
        }

        double endAngle = startAngle + (pct / 100.0 * 360.0);
        bool isLargeArc = pct > 50;

        var startPoint = AngleToPoint(centreX, centreY, radius, startAngle);
        var endPoint = AngleToPoint(centreX, centreY, radius, endAngle);

        if (pct >= 100)
        {
            var midPoint = AngleToPoint(centreX, centreY, radius, startAngle + 180);

            var fullGeometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
            figure.Segments.Add(new ArcSegment(midPoint, new Size(radius, radius), 0,
                true, SweepDirection.Clockwise, true));
            figure.Segments.Add(new ArcSegment(
                AngleToPoint(centreX, centreY, radius, startAngle + 359.99),
                new Size(radius, radius), 0,
                true, SweepDirection.Clockwise, true));
            fullGeometry.Figures.Add(figure);

            ArcPath.Data = fullGeometry;
        }
        else
        {
            var geometry = new PathGeometry();
            var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
            figure.Segments.Add(new ArcSegment(endPoint, new Size(radius, radius), 0,
                isLargeArc, SweepDirection.Clockwise, true));
            geometry.Figures.Add(figure);

            ArcPath.Data = geometry;
        }

        ArcPath.Stroke = data.StatusBrush;
    }

    private static Point AngleToPoint(double cx, double cy, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(
            cx + r * Math.Cos(rad),
            cy + r * Math.Sin(rad));
    }
}
