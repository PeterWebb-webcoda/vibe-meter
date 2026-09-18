using VibeMeter.Core;

namespace VibeMeter.Publishing;

/// <summary>
/// Completes a cycle's reports so the document mentions EVERY provider the host
/// knows about, stating a provider the host did not collect as
/// <see cref="ProviderState.Disabled"/> rather than leaving it out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why silence is not good enough.</b> The collection API keeps the latest
/// reading PER PROVIDER: it composes a machine's view by taking, for each
/// providerId, the first occurrence across snapshots ordered newest-first by
/// <c>observedAt</c>. A provider that is simply ABSENT from a document is
/// therefore not cleared — the previous stored reading survives untouched and
/// keeps being served. That is exactly what makes the freshness gate's
/// omissions safe (a fresher reading from another machine wins), and exactly
/// what makes a DISABLED provider dangerous: the tray app only fetches the
/// providers the user enabled, so turning one off would merely stop mentioning
/// it, and its final reading would sit on the phone looking current for ever.
/// </para>
/// <para>
/// Saying "disabled" out loud is the only way to retract it. The state is
/// already in the API's vocabulary (<see cref="SnapshotMapper"/> maps
/// <see cref="ProviderState.Disabled"/> to <c>"disabled"</c>) and a disabled
/// report carries no gauges, so the reader sees a provider that is deliberately
/// off rather than a stale percentage.
/// </para>
/// <para>
/// <b>Why this derives "disabled" from what was collected, not from the
/// enabled flag.</b> The claim the document makes is about what this host
/// actually reported, so the honest source is the collection itself: any known
/// provider missing from it gets stated rather than silently retained,
/// whatever the reason it went missing. Reading the user's toggle a second time
/// here would let the two drift — a host whose in-memory settings lag a save
/// would publish "enabled" for a provider it had stopped fetching, which is the
/// precise failure this exists to prevent.
/// </para>
/// </remarks>
public static class ProviderRoster
{
    /// <summary>
    /// Returns <paramref name="collected"/> extended with a
    /// <see cref="ProviderState.Disabled"/> report for every provider in
    /// <paramref name="known"/> that it does not mention.
    /// </summary>
    /// <param name="collected">The reports this cycle actually produced.</param>
    /// <param name="known">
    /// Every provider this host could have fetched — the registry, in its own
    /// order, which is what makes the result deterministic. Determinism is not
    /// cosmetic here: <see cref="ContentFingerprint"/> hashes the provider list
    /// INCLUDING its order, so a roster whose order wandered between cycles
    /// would read as new figures every time and defeat
    /// <see cref="PublishPolicy"/>.
    /// </param>
    public static IReadOnlyList<ProviderUsage> IncludingDisabled(
        IReadOnlyList<ProviderUsage> collected,
        IReadOnlyList<IUsageProvider> known)
    {
        // Provider ids are matched case-insensitively, as ProviderRegistry.Get
        // does, so a registry and a report that disagree on casing cannot
        // produce one entry for the reading and another claiming it is off.
        var reports = new Dictionary<string, ProviderUsage>(StringComparer.OrdinalIgnoreCase);
        foreach (var usage in collected)
        {
            if (usage is not null)
            {
                // First wins, matching SnapshotMapper's duplicate handling.
                reports.TryAdd(usage.ProviderId, usage);
            }
        }

        var placed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roster = new List<ProviderUsage>(known.Count + collected.Count);

        foreach (var provider in known)
        {
            if (provider is null || !placed.Add(provider.Id))
            {
                continue;
            }

            roster.Add(reports.TryGetValue(provider.Id, out var report) ? report : Disabled(provider));
        }

        // A report for something outside the known set should be impossible —
        // the host's cards come from the registry — but dropping a real reading
        // over a bookkeeping mismatch would be far worse than appending it.
        foreach (var usage in collected)
        {
            if (usage is not null && placed.Add(usage.ProviderId))
            {
                roster.Add(usage);
            }
        }

        return roster;
    }

    /// <summary>
    /// A provider stated as off. No gauges: there is no current reading to
    /// report, and an empty gauge list is what tells the reader so (the API
    /// accepts 0..16).
    /// </summary>
    private static ProviderUsage Disabled(IUsageProvider provider) => new()
    {
        ProviderId = provider.Id,
        DisplayName = provider.DisplayName,
        State = ProviderState.Disabled,
    };
}
