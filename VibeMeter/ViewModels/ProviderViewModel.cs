using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VibeMeter.Core;
using VibeMeter.Models;
using VibeMeter.Providers.Claude;
using VibeMeter.Providers.Codex;
using VibeMeter.Providers.Google;

namespace VibeMeter.ViewModels;

/// <summary>
/// Observable, UI-facing wrapper around one provider's latest
/// <see cref="ProviderUsage"/>. One card per provider in the main window.
/// </summary>
public sealed partial class ProviderViewModel : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }

    [ObservableProperty] private ProviderState _state;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _planLabel;
    [ObservableProperty] private int? _availableCount;
    [ObservableProperty] private string? _resetNote;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private string? _resetCreditsTooltip;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private DateTime? _lastFetched;
    [ObservableProperty] private object? _extensionData;

    // Cost Data
    [ObservableProperty] private string? _todayCostText;
    [ObservableProperty] private string? _weekCostText;
    [ObservableProperty] private string? _monthCostText;
    [ObservableProperty] private string? _todayTokensText;
    [ObservableProperty] private bool _hasCostData;
    [ObservableProperty] private bool _isCostDetailsExpanded;
    public ObservableCollection<CostModelBreakdownItem> CostModelBreakdowns { get; } = new();

    /// <summary>Remaining % of the primary (first) gauge — drives the compact micro-bar.</summary>
    [ObservableProperty] private int _primaryPercent;

    /// <summary>Short provider name for the compact bar.</summary>
    public string ShortName => Id switch
    {
        "codex"  => "Codex",
        "claude" => "Claude",
        "zai"    => "Z.ai",
        "google" => "Google",
        _        => DisplayName
    };

    [ObservableProperty] private int _fiveHourPercent;
    [ObservableProperty] private string _fiveHourReset = "";
    [ObservableProperty] private string? _fiveHourTooltip;
    [ObservableProperty] private int _weeklyPercent;
    [ObservableProperty] private string _weeklyReset = "";
    [ObservableProperty] private string? _weeklyTooltip;

    public ObservableCollection<UsageGaugeData> Gauges { get; } = new();

    public bool HasGauges => Gauges.Count > 0;
    public bool ShowNotConfigured => State == ProviderState.NotConfigured && !HasGauges;
    public bool ShowError => State == ProviderState.Error && !HasGauges;
    public bool ShowResetBank => AvailableCount.HasValue;

    /// <summary>
    /// Show the neutral notice box — a successful fetch that has no usage figure to display
    /// (see <see cref="ProviderUsage.Notice"/>). Not an error.
    /// </summary>
    public bool ShowNotice => !string.IsNullOrEmpty(Notice);
    public bool ShowDetailsButton => HasCostData;
    /// <summary>Show the account carousel controls (Google card with ≥2 accounts).</summary>
    public bool ShowAccountSwitcher => Id == "google" && GoogleProvider.Accounts.Count >= 2;

    /// <summary>Carousel position indicator, e.g. "1/2". Empty when not applicable.</summary>
    public string AccountPositionText => ShowAccountSwitcher
        ? $"{GoogleProvider.ActiveAccountIndex + 1}/{GoogleProvider.Accounts.Count}"
        : "";
    public bool IsEnabled { get; set; } = true;

    /// <summary>Brand accent colour for this provider's card.</summary>
    public Color Accent => ProviderAccent.For(Id);

    public SolidColorBrush AccentBrush => new(Accent);

    public ProviderViewModel(IUsageProvider provider)
    {
        Id = provider.Id;
        DisplayName = provider.DisplayName;
    }

    /// <summary>Pushes a fresh fetch result into the observable surface.</summary>
    public void Apply(ProviderUsage usage)
    {
        Gauges.Clear();
        foreach (var g in usage.Gauges)
        {
            Gauges.Add(new UsageGaugeData
            {
                Id = g.Id,
                Title = g.Title,
                Subtitle = g.Subtitle ?? "",
                Percent = g.PercentRemaining,
                ResetAt = g.ResetAt,
                TooltipText = g.TooltipText
            });
        }

        State = usage.State;
        ErrorMessage = usage.ErrorMessage;
        PlanLabel = usage.PlanLabel;
        AvailableCount = usage.AvailableCount;
        ResetNote = usage.ResetNote;
        Notice = usage.Notice;
        ResetCreditsTooltip = BuildCreditsTooltip(usage.ResetCredits);
        LastFetched = usage.FetchedAt;
        ExtensionData = usage.ExtensionData;
        StatusText = DeriveStatusText(usage);
        PrimaryPercent = Gauges.Count > 0 ? Gauges[0].Percent : 0;
        PopulateCompactGauges(usage.Gauges);
        PopulateCostData(usage.ExtensionData);

        OnPropertyChanged(nameof(HasGauges));
        OnPropertyChanged(nameof(ShowNotConfigured));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowResetBank));
        OnPropertyChanged(nameof(ShowNotice));
        OnPropertyChanged(nameof(ShowAccountSwitcher));
        OnPropertyChanged(nameof(AccountPositionText));
    }

    private void PopulateCostData(object? extensionData)
    {
        switch (extensionData)
        {
            case ClaudeCostDetailsData c:
                PopulateFromCost(
                    c.TodayTotalCostUsd, c.TodayTotalTokens,
                    c.WeekTotalCostUsd, c.MonthTotalCostUsd,
                    c.WeeklyModelCosts.Select(m => (m.ModelId, m.TotalCostUsd)));
                break;

            case CodexCostDetailsData x:
                PopulateFromCost(
                    x.TodayTotalCostUsd, x.TodayTotalTokens,
                    x.WeekTotalCostUsd, x.MonthTotalCostUsd,
                    x.WeeklyModelCosts.Select(m => (m.ModelId, m.TotalCostUsd)));
                break;

            default:
                HasCostData = false;
                CostModelBreakdowns.Clear();
                break;
        }
        OnPropertyChanged(nameof(ShowDetailsButton));
    }

    /// <summary>Shared populator — both Claude and Codex cost records carry the same shape.</summary>
    private void PopulateFromCost(
        decimal todayCost, long todayTokens,
        decimal weekCost, decimal monthCost,
        IEnumerable<(string ModelId, decimal TotalCostUsd)> weeklyModels)
    {
        HasCostData = true;
        TodayCostText = $"${todayCost:F2} today";
        WeekCostText = $"${weekCost:F2} this week";
        MonthCostText = $"${monthCost:F2} this month";
        TodayTokensText = FormatTokens(todayTokens) + " tokens";

        CostModelBreakdowns.Clear();
        decimal totalWeekCost = weekCost > 0 ? weekCost : 1;

        foreach (var (modelId, modelCost) in weeklyModels.Take(4))
        {
            if (modelCost <= 0.01m) continue;
            CostModelBreakdowns.Add(new CostModelBreakdownItem
            {
                ModelName = modelId,
                CostText = $"${modelCost:F2}",
                Percentage = (double)(modelCost / totalWeekCost)
            });
        }
    }

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.#}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}K";
        return tokens.ToString("N0");
    }

    private void PopulateCompactGauges(IReadOnlyList<UsageGauge> gauges)
    {
        UsageGauge? fiveH = null;
        UsageGauge? weekly = null;

        foreach (var g in gauges)
        {
            var lower = g.Title.ToLowerInvariant();
            if (lower.Contains("5h") || lower.Contains("5-hour") || lower.Contains("primary"))
                fiveH ??= g;
            else if (lower.Contains("week") || lower.Contains("month"))
                weekly ??= g;
        }

        fiveH ??= gauges.Count > 0 ? gauges[0] : null;
        weekly ??= gauges.Count > 1 ? gauges[1] : null;

        FiveHourPercent = fiveH?.PercentRemaining ?? 0;
        FiveHourReset = FormatShortCountdown(fiveH?.ResetAt);
        FiveHourTooltip = fiveH?.TooltipText;
        WeeklyPercent = weekly?.PercentRemaining ?? 0;
        WeeklyReset = FormatShortCountdown(weekly?.ResetAt);
        WeeklyTooltip = weekly?.TooltipText;
    }

    private static string FormatShortCountdown(DateTime? resetAt)
    {
        if (!resetAt.HasValue) return "";
        var span = resetAt.Value - DateTime.Now;
        if (span.TotalSeconds <= 0) return "now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m";
        if (span.TotalHours < 24)
        {
            var h = span.TotalHours;
            return h >= 10 ? $"{(int)h}h" : $"{h:0.#}h";
        }
        var d = span.TotalDays;
        return d >= 10 ? $"{(int)d}d" : $"{d:0.#}d";
    }

    private static string? BuildCreditsTooltip(IReadOnlyList<ResetCredit> credits)
    {
        if (credits.Count == 0) return null;

        var lines = new List<string> { "Reset Credits:" };
        for (int i = 0; i < credits.Count; i++)
        {
            var c = credits[i];
            var daysLeft = (int)Math.Ceiling((c.ExpiresAt - DateTime.Now).TotalDays);
            string expiry = daysLeft switch
            {
                <= 0 => "expired",
                1    => $"expires tomorrow ({c.ExpiresAt:MMM d})",
                _    => $"expires {c.ExpiresAt:MMM d} ({daysLeft} days)"
            };
            lines.Add($"  Credit {i + 1}: {expiry}");
        }
        return string.Join("\n", lines);
    }

    private static string DeriveStatusText(ProviderUsage usage) => usage.State switch
    {
        ProviderState.Ok            => usage.ErrorMessage is not null ? "Updated (partial)" : $"Updated {usage.FetchedAt:t}",
        ProviderState.NotConfigured => "Not configured",
        ProviderState.Error         => "Error",
        ProviderState.Disabled      => "Disabled",
        _                           => ""
    };
}

/// <summary>Brand accent colours keyed by provider id.</summary>
public static class ProviderAccent
{
    public static Color For(string providerId) => providerId switch
    {
        "codex"   => Color.FromRgb(64, 194, 232),   // aurora blue
        "claude"  => Color.FromRgb(217, 119, 87),   // Anthropic coral
        "zai"     => Color.FromRgb(124, 92, 255),   // GLM purple
        "google"  => Color.FromRgb(66, 133, 244),   // Google blue
        _         => Color.FromRgb(160, 176, 192)
    };
}

public sealed class CostModelBreakdownItem
{
    public string ModelName { get; init; } = "";
    public string CostText { get; init; } = "";
    
    /// <summary>Value from 0.0 to 1.0 representing proportion of total cost</summary>
    public double Percentage { get; init; }

    /// <summary>For binding to Grid column width or ProgressBar</summary>
    public double Percentage100 => Percentage * 100;
}
