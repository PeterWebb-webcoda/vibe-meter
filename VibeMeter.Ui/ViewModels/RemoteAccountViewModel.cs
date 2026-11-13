using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using VibeMeter.Publishing;
using VibeMeter.Ui.Models;

namespace VibeMeter.Ui.ViewModels;

/// <summary>
/// One read-only card for an account another machine publishes — e.g. the personal Claude
/// on a Linux box, shown beneath this PC's own Claude card. Built from a
/// <see cref="RemoteAccount"/> read back from the collection API.
/// </summary>
/// <remarks>
/// Display only, and immutable: a fresh read or a refresh replaces the whole card. It is
/// never a <see cref="ProviderViewModel"/> and never feeds the publish path, the compact
/// bar or the cost figures — those are all built from this machine's own readings.
/// </remarks>
public sealed class RemoteAccountViewModel
{
    /// <summary>Card opacity for a reading past <see cref="RemoteAccountFreshness.StaleAfter"/>.</summary>
    public const double StaleOpacity = 0.55;

    private RemoteAccountViewModel(
        string providerId,
        string accountLabel,
        string title,
        string? planLabel,
        IEnumerable<UsageGaugeData> gauges,
        string note,
        bool isStale)
    {
        ProviderId = providerId;
        AccountLabel = accountLabel;
        Title = title;
        PlanLabel = planLabel;
        Gauges = new ObservableCollection<UsageGaugeData>(gauges);
        Note = note;
        IsStale = isStale;
    }

    public string ProviderId { get; }

    public string AccountLabel { get; }

    /// <summary>E.g. "Claude Code · Personal".</summary>
    public string Title { get; }

    public string? PlanLabel { get; }

    public bool HasPlanLabel => !string.IsNullOrEmpty(PlanLabel);

    /// <summary>Rendered with the same meter templates as a local card's gauges.</summary>
    public ObservableCollection<UsageGaugeData> Gauges { get; }

    public bool HasGauges => Gauges.Count > 0;

    /// <summary>E.g. "From another machine · observed 4 min ago".</summary>
    public string Note { get; }

    public bool IsStale { get; }

    public double CardOpacity => IsStale ? StaleOpacity : 1.0;

    /// <summary>The same brand accent as the local card of the provider.</summary>
    public Rgb Accent => ProviderAccent.For(ProviderId);

    /// <summary>
    /// Builds the card for <paramref name="account"/> as it should look at
    /// <paramref name="now"/>, or returns null when the reading is too old to show
    /// (<see cref="RemoteAccountAge.Expired"/>).
    /// </summary>
    /// <param name="providerDisplayName">The local card's name for the provider, e.g. "Claude Code".</param>
    public static RemoteAccountViewModel? From(RemoteAccount account, string providerDisplayName, DateTimeOffset now)
    {
        var age = RemoteAccountFreshness.Classify(account.ObservedAt, now);
        if (age == RemoteAccountAge.Expired)
        {
            return null;
        }

        var ago = FormatAgo(now - account.ObservedAt!.Value);
        var note = age == RemoteAccountAge.Stale
            ? $"From another machine · last seen {ago}"
            : $"From another machine · observed {ago}";

        return new RemoteAccountViewModel(
            account.ProviderId,
            account.AccountLabel,
            $"{providerDisplayName} · {Capitalise(account.AccountLabel)}",
            string.IsNullOrWhiteSpace(account.PlanLabel) ? null : account.PlanLabel.Trim(),
            account.Gauges.Select(ToGaugeData).OfType<UsageGaugeData>(),
            note,
            age == RemoteAccountAge.Stale);
    }

    /// <summary>"personal" → "Personal". Labels are lowercase ASCII by contract.</summary>
    internal static string Capitalise(string label) =>
        label.Length == 0 ? label : char.ToUpperInvariant(label[0]) + label[1..];

    /// <summary>"just now", "4 min ago", "2 h ago". A future stamp (clock skew) reads as "just now".</summary>
    public static string FormatAgo(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1))
        {
            return "just now";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes} min ago";
        }

        if (age < TimeSpan.FromDays(2))
        {
            return $"{(int)age.TotalHours} h ago";
        }

        return $"{(int)age.TotalDays} d ago";
    }

    /// <summary>
    /// Maps one remote gauge onto the meter model. A gauge with no figure is left out
    /// rather than drawn as 0 % — an empty red ring would claim the account is exhausted.
    /// </summary>
    private static UsageGaugeData? ToGaugeData(RemoteGauge gauge)
    {
        if (gauge.PercentRemaining is not { } percent)
        {
            return null;
        }

        return new UsageGaugeData
        {
            Id = gauge.Id ?? "",
            Title = gauge.Title ?? gauge.Id ?? "",
            Subtitle = gauge.Subtitle ?? "",

            // Already "remaining", exactly as the local gauges and the publisher use it.
            Percent = (int)Math.Round(Math.Clamp(percent, 0m, 100m), MidpointRounding.AwayFromZero),

            // Local time, as every local gauge carries it: UsageGaugeData compares it
            // with DateTime.Now to word "resets tomorrow 9:59 AM".
            ResetAt = gauge.ResetAt?.LocalDateTime,
        };
    }
}
