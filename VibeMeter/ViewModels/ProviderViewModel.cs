using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using VibeMeter.Core;
using VibeMeter.Models;

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
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private DateTime? _lastFetched;

    public ObservableCollection<UsageGaugeData> Gauges { get; } = new();

    public bool HasGauges => Gauges.Count > 0;
    public bool ShowNotConfigured => State == ProviderState.NotConfigured && !HasGauges;
    public bool ShowError => State == ProviderState.Error && !HasGauges;
    public bool ShowResetBank => AvailableCount.HasValue;
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
                ResetAt = g.ResetAt
            });
        }

        State = usage.State;
        ErrorMessage = usage.ErrorMessage;
        PlanLabel = usage.PlanLabel;
        AvailableCount = usage.AvailableCount;
        ResetNote = usage.ResetNote;
        LastFetched = usage.FetchedAt;
        StatusText = DeriveStatusText(usage);

        OnPropertyChanged(nameof(HasGauges));
        OnPropertyChanged(nameof(ShowNotConfigured));
        OnPropertyChanged(nameof(ShowError));
        OnPropertyChanged(nameof(ShowResetBank));
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
