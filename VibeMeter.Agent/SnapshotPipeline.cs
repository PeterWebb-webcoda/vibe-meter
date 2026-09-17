using System.Diagnostics;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;

namespace VibeMeter.Agent;

/// <summary>
/// Everything one cycle produces before anything goes near the collection API:
/// the raw provider reports, the freshness verdict, the publish timestamp and
/// the mapped snapshot. Produced by <see cref="SnapshotPipeline"/>, consumed by
/// the daemon cycle (to publish) and the --dry-run path (to print).
/// </summary>
/// <param name="Collected">Every provider's report this cycle, in registry order.</param>
/// <param name="Freshness">The freshness gate's split of <paramref name="Collected"/>.</param>
/// <param name="ObservedAt">The publish timestamp stamped on the mapped snapshot.</param>
/// <param name="Mapped">The snapshot document exactly as it would hit the wire.</param>
internal sealed record CycleCollection(
    IReadOnlyList<ProviderUsage> Collected,
    FreshnessGateResult Freshness,
    DateTimeOffset ObservedAt,
    MappedSnapshot Mapped);

/// <summary>
/// The collect-freshness-map half of an agent cycle — the half that never
/// touches the collection API. Shared verbatim by the daemon loop
/// (<see cref="AgentHost"/>) and --dry-run (<see cref="DryRunRunner"/>) so a
/// dry-run exercises byte-for-byte the same collection, freshness and mapping
/// rules a real publish would. A null result means the cycle could not produce
/// a snapshot; the reason is already logged.
/// </summary>
internal sealed class SnapshotPipeline
{
    // A provider that neither returns nor throws within this window is reported
    // as an error for the cycle, keeping the interval honest. (IUsageProvider.
    // FetchAsync predates the agent and takes no CancellationToken.)
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly SnapshotMapper _mapper;
    private readonly FreshnessGate _freshnessGate;

    public SnapshotPipeline(IReadOnlyList<IUsageProvider> providers, SnapshotMapper mapper, TimeSpan stalenessThreshold)
    {
        _providers = providers;
        _mapper = mapper;
        _freshnessGate = new FreshnessGate(stalenessThreshold);
    }

    /// <summary>One collect-gate-map pass. Deterministic and side-effect free
    /// beyond logging: nothing here publishes or queues anything.</summary>
    internal async Task<CycleCollection?> CollectGateMapAsync(CancellationToken cancellationToken)
    {
        List<ProviderUsage> usages;
        try
        {
            usages = await CollectAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Provider collection failed: {ex.Message}");
            return null;
        }

        // Omit reports whose UNDERLYING data is too old, before anything is
        // mapped or stamped with this cycle's publish time. The gate's class
        // comment explains why omission - never a state="stale" downgrade - is
        // the only correct treatment under the server's newest-first merge.
        FreshnessGateResult freshness;
        try
        {
            freshness = _freshnessGate.Apply(usages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Freshness check failed: {ex.Message}");
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
            AgentLog.Error($"Snapshot mapping failed: {ex.Message}");
            return null;
        }

        return new CycleCollection(usages, freshness, observedAt, mapped);
    }

    private async Task<List<ProviderUsage>> CollectAsync(CancellationToken cancellationToken)
    {
        var fetches = _providers.Select(async provider =>
        {
            try
            {
                // WaitAsync both bounds a hung provider and lets shutdown
                // interrupt an in-flight fetch despite FetchAsync taking no token.
                return await provider.FetchAsync().WaitAsync(FetchTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                AgentLog.Error($"Provider '{provider.Id}' did not respond within {FetchTimeout.TotalSeconds:0}s.");
                return ErrorUsage(provider);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Providers promise not to throw for expected conditions; this is
                // the belt-and-braces path so one rogue provider can never end the process.
                AgentLog.Error($"Provider '{provider.Id}' fetch failed: {ex.Message}");
                return ErrorUsage(provider);
            }
        });

        return [.. await Task.WhenAll(fetches)];
    }

    private static ProviderUsage ErrorUsage(IUsageProvider provider) => new()
    {
        ProviderId = provider.Id,
        DisplayName = provider.DisplayName,
        State = ProviderState.Error,
    };
}
