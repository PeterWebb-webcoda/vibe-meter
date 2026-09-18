using VibeMeter.Core;

namespace VibeMeter.Publishing;

/// <summary>
/// Everything one cycle produces before anything goes near the collection API:
/// the raw provider reports, the freshness verdict, the publish timestamp and
/// the mapped snapshot. Produced by <see cref="SnapshotComposer"/>, consumed by
/// <see cref="SnapshotPublishCycle"/> (to publish) and by the agent's --dry-run
/// path (to print).
/// </summary>
/// <param name="Collected">Every provider's report this cycle, in the order the host collected them.</param>
/// <param name="Freshness">The freshness gate's split of <paramref name="Collected"/>.</param>
/// <param name="ObservedAt">The publish timestamp stamped on the mapped snapshot.</param>
/// <param name="Mapped">The snapshot document exactly as it would hit the wire.</param>
public sealed record CycleCollection(
    IReadOnlyList<ProviderUsage> Collected,
    FreshnessGateResult Freshness,
    DateTimeOffset ObservedAt,
    MappedSnapshot Mapped);

/// <summary>
/// The freshness-and-mapping half of a cycle — the half that touches neither
/// the collection API nor the offline queue. Shared verbatim by
/// <see cref="SnapshotPublishCycle"/> and by the agent's --dry-run, so a
/// dry-run exercises byte-for-byte the same freshness and mapping rules a real
/// publish would. A null result means the cycle could not produce a snapshot;
/// the reason is already logged.
/// </summary>
/// <remarks>
/// One composer instance means one <see cref="FreshnessGate"/>, and the gate
/// notes an unmeasurable provider only the first time it sees it — so a host
/// keeps a single composer for its lifetime rather than building one per cycle.
/// </remarks>
public sealed class SnapshotComposer
{
    private readonly SnapshotMapper _mapper;
    private readonly FreshnessGate _freshnessGate;
    private readonly IPublishLog _log;

    public SnapshotComposer(SnapshotMapper mapper, TimeSpan stalenessThreshold, IPublishLog log)
    {
        _mapper = mapper;
        _freshnessGate = new FreshnessGate(stalenessThreshold);
        _log = log;
    }

    /// <summary>One gate-map pass over an already-collected cycle.
    /// Deterministic and side-effect free beyond logging: nothing here
    /// publishes or queues anything.</summary>
    public CycleCollection? Compose(IReadOnlyList<ProviderUsage> collected)
    {
        // Omit reports whose UNDERLYING data is too old, before anything is
        // mapped or stamped with this cycle's publish time. The gate's class
        // comment explains why omission - never a state="stale" downgrade - is
        // the only correct treatment under the server's newest-first merge.
        FreshnessGateResult freshness;
        try
        {
            freshness = _freshnessGate.Apply(collected);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Freshness check failed: {ex.Message}");
            return null;
        }

        // Always UTC with zero offset - the API rejects any other offset.
        var observedAt = DateTimeOffset.UtcNow;

        MappedSnapshot mapped;
        try
        {
            mapped = _mapper.Map(freshness.Publishable, observedAt);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Error($"Snapshot mapping failed: {ex.Message}");
            return null;
        }

        return new CycleCollection(collected, freshness, observedAt, mapped);
    }
}
