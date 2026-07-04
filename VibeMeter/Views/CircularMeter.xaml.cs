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
            ResetTrack.Visibility = Visibility.Collapsed;
            ResetArcPath.Visibility = Visibility.Collapsed;
            return;
        }

        PercentText.Text = data.ClampedPercent.ToString();
        TitleText.Text = data.Title;
        SubtitleText.Text = data.Subtitle;
        ResetTextBlock.Text = data.ResetText;

        const double centreX = 27;
        const double centreY = 27;
        const double radius = 24;
        const double startAngle = -90.0; // 12 o'clock

        int pct = data.ClampedPercent;
        if (pct <= 0)
        {
            ArcPath.Data = null;
        }
        else
        {
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

        // Draw reset countdown inner circle if possible
        double? resetPct = null;
        if (data.ResetAt.HasValue)
        {
            var totalDuration = GetTotalDuration(data.Id, data.Title);
            if (totalDuration.HasValue)
            {
                var timeRemaining = data.ResetAt.Value - DateTime.Now;
                if (timeRemaining > TimeSpan.Zero)
                {
                    resetPct = Math.Max(0.0, Math.Min(100.0, (timeRemaining.TotalSeconds / totalDuration.Value.TotalSeconds) * 100.0));
                }
                else
                {
                    resetPct = 0.0;
                }
            }
        }

        if (resetPct.HasValue)
        {
            ResetTrack.Visibility = Visibility.Visible;
            ResetArcPath.Visibility = Visibility.Visible;

            const double rRadius = 17;
            const double rStartAngle = -90.0;

            double rpct = resetPct.Value;
            if (rpct <= 0)
            {
                ResetArcPath.Data = null;
            }
            else
            {
                double endAngle = rStartAngle + (rpct / 100.0 * 360.0);
                bool isLargeArc = rpct > 50;

                var startPoint = AngleToPoint(centreX, centreY, rRadius, rStartAngle);
                var endPoint = AngleToPoint(centreX, centreY, rRadius, endAngle);

                if (rpct >= 100)
                {
                    var midPoint = AngleToPoint(centreX, centreY, rRadius, rStartAngle + 180);

                    var fullGeometry = new PathGeometry();
                    var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
                    figure.Segments.Add(new ArcSegment(midPoint, new Size(rRadius, rRadius), 0,
                        true, SweepDirection.Clockwise, true));
                    figure.Segments.Add(new ArcSegment(
                        AngleToPoint(centreX, centreY, rRadius, rStartAngle + 359.99),
                        new Size(rRadius, rRadius), 0,
                        true, SweepDirection.Clockwise, true));
                    fullGeometry.Figures.Add(figure);

                    ResetArcPath.Data = fullGeometry;
                }
                else
                {
                    var geometry = new PathGeometry();
                    var figure = new PathFigure { StartPoint = startPoint, IsClosed = false };
                    figure.Segments.Add(new ArcSegment(endPoint, new Size(rRadius, rRadius), 0,
                        isLargeArc, SweepDirection.Clockwise, true));
                    geometry.Figures.Add(figure);

                    ResetArcPath.Data = geometry;
                }
            }

            // A clean, premium semi-transparent white brush that matches the styling theme
            ResetArcPath.Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            ResetTrack.Visibility = Visibility.Collapsed;
            ResetArcPath.Visibility = Visibility.Collapsed;
        }
    }

    private static TimeSpan? GetTotalDuration(string gaugeId, string title)
    {
        var lowerId = gaugeId.ToLowerInvariant();
        var lowerTitle = title.ToLowerInvariant();

        if (lowerId.Contains("weekly") || lowerId.Contains("week") || lowerTitle.Contains("week"))
            return TimeSpan.FromDays(7);
        if (lowerId.Contains("monthly") || lowerId.Contains("month") || lowerTitle.Contains("month"))
            return TimeSpan.FromDays(30);
        if (lowerId.Contains("5h") || lowerId.Contains("5-hour") || lowerTitle.Contains("5h") || lowerTitle.Contains("5-hour"))
            return TimeSpan.FromHours(5);
        if (lowerId.Contains("spark") || lowerTitle.Contains("spark"))
            return TimeSpan.FromHours(5);

        // Fallback: parse format like "3h" or "12h" or "2d" from the title
        var match = System.Text.RegularExpressions.Regex.Match(lowerTitle, @"^(\d+)(h|d)$");
        if (match.Success)
        {
            var val = int.Parse(match.Groups[1].Value);
            var unit = match.Groups[2].Value;
            if (unit == "h") return TimeSpan.FromHours(val);
            if (unit == "d") return TimeSpan.FromDays(val);
        }

        return null;
    }

    private static Point AngleToPoint(double cx, double cy, double r, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180.0;
        return new Point(
            cx + r * Math.Cos(rad),
            cy + r * Math.Sin(rad));
    }
}
