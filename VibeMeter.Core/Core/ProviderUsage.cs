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
/// <param name="ResetWindowSeconds">
/// Length of the window this gauge measures, in seconds — but only when the
/// provider genuinely reports it (e.g. Codex's <c>limit_window_seconds</c>, or a
/// window the provider's own data names, such as Claude's five-hour / seven-day
/// fields). <see langword="null"/> means the provider does not state a length;
/// it must never be inferred from the gauge's id, title or display strings.
/// </param>
public sealed record UsageGauge(
    string Id,
    string Title,
    string? Subtitle,
    int PercentRemaining,
    DateTime? ResetAt,
    string? TooltipText = null,
    int? ResetWindowSeconds = null);

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

    /// <summary>
    /// When the provider's UNDERLYING data was observed at its source — for a
    /// file-derived provider such as Claude, when the source surface last
    /// refreshed the local figures. <see langword="null"/> when the observation
    /// is inherently current (fetched live over HTTP each cycle) or cannot be
    /// determined; the agent's freshness gate treats null as fresh rather than
    /// dropping data.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="FetchedAt"/> (when this machine
    /// polled) and from the wire snapshot's <c>observedAt</c> (publish time):
    /// the collection API merges per provider newest-first by publish time, so
    /// publishing old underlying data stamped "now" would mask a fresher
    /// reading from another machine — which is why the agent omits stale
    /// observations instead of reporting them as <c>state="stale"</c>.
    /// </remarks>
    public DateTimeOffset? SourceObservedAt { get; init; }

    /// <summary>
    /// Which local surface supplied the figures, for a provider that can read from more
    /// than one (Claude reads either the Claude Code CLI cache or the desktop app's sampled
    /// history). <see langword="null"/> when the provider has only one source, or fetches
    /// live over HTTP.
    /// </summary>
    /// <remarks>
    /// Purely informational, and deliberately additive: it is logged, and shown in the
    /// card's tooltip, but it is NOT published — the collection API's snapshot contract has
    /// no field for it, and the mapper reads only id, state, plan label and gauges. It
    /// exists because "the provider was omitted as stale" is a different problem depending
    /// on which of two sources was being judged, and nothing downstream could previously
    /// tell them apart.
    /// </remarks>
    public string? SourceLabel { get; init; }

    /// <summary>
    /// Why the provider's PREFERRED source did not supply the figures, when a lesser one did
    /// — for Claude, why the live Anthropic usage API produced no reading this cycle and a
    /// local file was used instead. <see langword="null"/> when the preferred source was the
    /// one used, or when the provider has only one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Purely diagnostic and deliberately additive, like <see cref="SourceLabel"/>: the hosts
    /// log it once each time it appears, changes or clears, the freshness gate quotes it
    /// inside every staleness omission it leads to, and it is never shown on a card and never
    /// published. It exists because a preferred source that fails on EVERY cycle is otherwise
    /// invisible: the fallback keeps the card working, and the only symptom is the fallback's
    /// age tripping the freshness gate — a log line that names the file that was too old and
    /// says nothing about why the source that is never too old was not used. That is exactly
    /// how the live Claude source was found to be failing silently for an hour on a machine
    /// whose console agent appeared to work.
    /// </para>
    /// <para>
    /// SECURITY: a fixed description or an HTTP status code. Never a token, a header or a
    /// response body — the same rule as every other string that reaches a log.
    /// </para>
    /// </remarks>
    public string? SourceDiagnostic { get; init; }

    /// <summary>Convenience factory for a "not yet implemented / coming soon" provider.</summary>
    public static ProviderUsage ComingSoon(string id, string displayName, string note) => new()
    {
        ProviderId = id,
        DisplayName = displayName,
        State = ProviderState.NotConfigured,
        ErrorMessage = note
    };
}
