using VibeMeter.Core;

namespace VibeMeter.Publishing;

/// <summary>
/// The outcome of one freshness pass: the reports worth publishing, one line of
/// explanation per omitted report, and one notice per provider whose freshness
/// could not be determined (logged once per process, not every cycle).
/// </summary>
/// <param name="Publishable">Reports that may proceed to mapping and publish.</param>
/// <param name="Omissions">Why each stale report was left out, for the log.</param>
/// <param name="Notes">Why each unknown-freshness report was kept, for the log.</param>
public sealed record FreshnessGateResult(
    IReadOnlyList<ProviderUsage> Publishable,
    IReadOnlyList<string> Omissions,
    IReadOnlyList<string> Notes);

/// <summary>
/// Omits provider reports whose UNDERLYING data is older than the staleness
/// threshold, regardless of when the agent polled.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why omission, not <c>state="stale"</c>.</b> The collection API composes
/// each provider's latest view by taking, per providerId, the FIRST occurrence
/// across snapshots ordered NEWEST-FIRST by <c>observedAt</c> — and
/// <c>observedAt</c> is the publish time, not the data's own time. An idle
/// machine whose Claude cache is days old still stamps its snapshot "now", so
/// publishing that reading would WIN the merge and mask the fresh reading from
/// the machine actually being used. The merge is time-ordered and ignores the
/// state field, so downgrading to <c>state="stale"</c> changes nothing; the
/// only way the fresh machine's reading can win is for this agent to say
/// nothing at all about that provider this cycle.
/// </para>
/// <para>
/// Freshness comes from <see cref="ProviderUsage.SourceObservedAt"/>: the
/// source's own observation time for file-derived providers (Claude), null for
/// providers fetched live over HTTP each cycle (Codex, Z.ai — inherently
/// current). A null timestamp on a successful fetch means freshness could not
/// be determined; the report is KEPT and a note logged, because an inability
/// to measure age must never silently erase data. Only successful fetches are
/// gated — NotConfigured, Disabled and Error keep their existing behaviour.
/// </para>
/// <para>
/// "Older than the threshold" is strict: an observation exactly
/// <see cref="_stalenessThreshold"/> old is still fresh. A future observation
/// (clock skew) is negative age, and stays fresh.
/// </para>
/// </remarks>
public sealed class FreshnessGate
{
    private readonly TimeSpan _stalenessThreshold;

    /// <summary>
    /// Providers already noted as having no source observation time — so the
    /// note is logged once per process rather than on every cycle. The host
    /// applies the gate sequentially, so no synchronisation is needed.
    /// </summary>
    private readonly HashSet<string> _freshnessUnknownNoted = new(StringComparer.Ordinal);

    public FreshnessGate(TimeSpan stalenessThreshold)
    {
        if (stalenessThreshold <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stalenessThreshold),
                stalenessThreshold,
                "The staleness threshold must be positive.");
        }

        _stalenessThreshold = stalenessThreshold;
    }

    /// <summary>
    /// Splits this cycle's reports into publishable and omitted, using the
    /// current UTC time unless the caller supplies a fixed clock (tests do).
    /// </summary>
    public FreshnessGateResult Apply(IReadOnlyList<ProviderUsage> usages, DateTimeOffset? now = null)
    {
        now ??= DateTimeOffset.UtcNow;

        var publishable = new List<ProviderUsage>();
        var omissions = new List<string>();
        var notes = new List<string>();

        foreach (var usage in usages)
        {
            // Nulls are the mapper's long-standing concern; it skips them too.
            if (usage is null)
            {
                continue;
            }

            // Only a successful fetch carries data whose age matters. States
            // with their own wire meaning (not-configured, disabled, error)
            // keep their existing behaviour; Loading never survives a completed
            // agent fetch.
            if (usage.State != ProviderState.Ok)
            {
                publishable.Add(usage);
                continue;
            }

            if (usage.SourceObservedAt is not { } observedAt)
            {
                // Freshness could not be determined. Keep the report — a missed
                // timestamp must not silently erase a provider — but say so, once
                // per provider, so the gap is visible without spamming every cycle.
                publishable.Add(usage);
                if (_freshnessUnknownNoted.Add(usage.ProviderId))
                {
                    notes.Add(
                        $"Provider '{usage.ProviderId}' reports no source observation time; " +
                        "treating it as fresh. Expected for providers fetched live over HTTP — " +
                        "a file-derived provider should set SourceObservedAt.");
                }

                continue;
            }

            var age = now.Value - observedAt;
            if (age > _stalenessThreshold)
            {
                omissions.Add(
                    $"Provider '{usage.ProviderId}' omitted: its underlying data was observed " +
                    $"{DescribeAge(age)} ago ({observedAt:yyyy-MM-dd HH:mm:ss zzz}), older than the " +
                    $"{DescribeThreshold(_stalenessThreshold)} staleness threshold. Omitted rather than " +
                    "marked state=\"stale\" because the server's newest-first merge keys on observedAt " +
                    "(publish time) and would let this snapshot mask a fresher reading from another machine.");
                continue;
            }

            publishable.Add(usage);
        }

        return new FreshnessGateResult(publishable, omissions, notes);
    }

    private static string DescribeAge(TimeSpan age) =>
        age.TotalHours >= 1 ? $"{age.TotalHours:0.#}h"
        : age.TotalMinutes >= 1 ? $"{age.TotalMinutes:0.#} min"
        : $"{age.TotalSeconds:0.#} s";

    private static string DescribeThreshold(TimeSpan threshold) =>
        threshold >= TimeSpan.FromMinutes(1)
            ? $"{threshold.TotalMinutes:0.#} minute"
            : $"{threshold.TotalSeconds:0.#} second";
}
