using VibeMeter.Core;
using VibeMeter.Publishing;

namespace VibeMeter.Agent;

/// <summary>
/// The headless collection loop. Every interval: flush any queued snapshots,
/// fetch every provider in parallel, then hand the reports to the shared
/// publish cycle, which gates, maps, publishes — and queues the document
/// whenever the API cannot take it. No failure path is allowed to escape a
/// cycle: a rogue provider, a dead network, or a full disk is logged and the
/// process keeps running.
/// A cycle is not the same thing as a row: the <see cref="PublishPolicy"/>
/// below decides which cycles are worth writing, so a short interval costs
/// provider fetches rather than database rows.
/// </summary>
public sealed class AgentHost
{
    private readonly AgentConfig _config;
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly OfflineQueue _queue;
    private readonly ProviderCollector _collector;
    private readonly SnapshotPublishCycle _cycle;

    public AgentHost(
        AgentConfig config,
        IReadOnlyList<IUsageProvider> providers,
        SnapshotMapper mapper,
        ISnapshotPublisher publisher,
        OfflineQueue queue)
    {
        _config = config;
        _providers = providers;
        _queue = queue;
        _collector = new ProviderCollector(providers);
        _cycle = new SnapshotPublishCycle(
            new SnapshotComposer(mapper, config.StalenessThreshold, AgentPublishLog.Instance),
            publisher,
            queue,
            AgentPublishLog.Instance,
            // The state is FILE-backed here, not in memory, because this host is
            // restarted by things other than a person: a service manager after
            // an upgrade, a supervisor after a crash, a scheduled --once on its
            // own timer. An in-memory policy would forget its baseline on every
            // one of those and publish afresh, so a crash-looping agent would
            // out-write the loop the policy exists to slow down. It lives in the
            // offline-queue directory this host already owns - see
            // FilePublishPolicyStore for why there and nowhere else.
            new PublishPolicy(FilePublishPolicyStore.ForQueue(queue)));
    }

    /// <summary>
    /// Runs until <paramref name="cancellationToken"/> is cancelled. Returns
    /// normally on shutdown; only an unexpected bug would throw.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        AgentLog.Info($"Agent starting - publishing to {_config.ApiBaseUrl} every {(int)_config.Interval.TotalSeconds}s.");
        AgentLog.Info($"Providers: {string.Join(", ", _providers.Select(p => p.Id))}. Offline queue: {_config.QueueDirectory} (capacity {_queue.Capacity}).");

        using var timer = new PeriodicTimer(_config.Interval);
        try
        {
            while (true)
            {
                await RunOneCycleAsync(cancellationToken);
                if (!await timer.WaitForNextTickAsync(cancellationToken))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C / SIGTERM - the normal way this loop ends.
        }

        AgentLog.Info("Agent stopping.");
    }

    /// <summary>One collect-gate-map-publish pass. Returns what became of the
    /// cycle's snapshot so <c>--once</c> can turn it into an exit code; tests
    /// can also run a single deterministic cycle instead of racing the loop.</summary>
    internal async Task<CycleOutcome> RunOneCycleAsync(CancellationToken cancellationToken)
    {
        // Oldest queued data leaves first so the API sees chronology - and
        // before collection, so a backlog is not held up behind four provider
        // fetches.
        try
        {
            await FlushQueueAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Offline-queue flush failed: {ex.Message}");
        }

        var collected = await _collector.CollectAsync(cancellationToken);
        if (collected is null)
        {
            return CycleOutcome.CollectionFailed;
        }

        return await _cycle.PublishAsync(collected, cancellationToken);
    }

    /// <summary>
    /// Sends queued snapshots oldest-first; see
    /// <see cref="SnapshotPublishCycle.FlushQueueAsync"/> for the retention
    /// rules. Exposed here because shutdown drains the queue one last time.
    /// </summary>
    public Task FlushQueueAsync(CancellationToken cancellationToken) =>
        _cycle.FlushQueueAsync(cancellationToken);
}
