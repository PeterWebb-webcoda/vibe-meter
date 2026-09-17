using System;
using System.Collections.Generic;

namespace VibeMeter.Core;

/// <summary>
/// Lifecycle / health state of a single provider's last fetch.
/// </summary>
public enum ProviderState
{
    /// <summary>Data fetched successfully.</summary>
    Ok,

    /// <summary>Fetch in progress.</summary>
    Loading,

    /// <summary>The provider is present but not set up (e.g. no local auth file / API key).</summary>
    NotConfigured,

    /// <summary>The last fetch failed.</summary>
    Error,

    /// <summary>Disabled by the user.</summary>
    Disabled
}

/// <summary>
/// One normalised usage gauge (a rate-limit window, a quota, etc.).
/// Pure data — UI concerns live in <c>UsageGaugeData</c>.
/// </summary>
public sealed record UsageGauge(
    string Id,
    string Title,
    string? Subtitle,
    int PercentRemaining,
    DateTime? ResetAt,
    string? TooltipText = null);

/// <summary>
/// One normalised reset-credit entry (e.g. a Codex rate-limit reset credit).
/// </summary>
public sealed record ResetCredit(
    string Status,
    DateTime GrantedAt,
    DateTime ExpiresAt,
    DateTime? RedeemedAt);

/// <summary>
/// The normalised result of a provider fetch, independent of any specific API shape.
/// </summary>
public sealed class ProviderUsage
{
    public string ProviderId { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public ProviderState State { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PlanLabel { get; init; }
    public int? AvailableCount { get; init; }
    public string? ResetNote { get; init; }

    /// <summary>
    /// A neutral, informational message shown on the card — for a successful fetch that
    /// nonetheless has no usage figure to display (e.g. a plan whose API reports no real
    /// quota data). Distinct from <see cref="ErrorMessage"/>: nothing has gone wrong, so it
    /// is not logged and not painted as a failure.
    /// </summary>
    public string? Notice { get; init; }

    public IReadOnlyList<ResetCredit> ResetCredits { get; init; } = Array.Empty<ResetCredit>();
    public IReadOnlyList<UsageGauge> Gauges { get; init; } = Array.Empty<UsageGauge>();
    public object? ExtensionData { get; init; }
    public DateTime FetchedAt { get; init; } = DateTime.Now;

    /// <summary>Convenience factory for a "not yet implemented / coming soon" provider.</summary>
    public static ProviderUsage ComingSoon(string id, string displayName, string note) => new()
    {
        ProviderId = id,
        DisplayName = displayName,
        State = ProviderState.NotConfigured,
        ErrorMessage = note
    };
}
