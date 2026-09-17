using System.Diagnostics;
using VibeMeter.Agent.AccessToken;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;

namespace VibeMeter.Agent;

/// <summary>
/// What became of one cycle's snapshot — the values <c>--once</c> translates
/// into a process exit code.
/// </summary>
internal enum CycleOutcome
{
    /// <summary>The API accepted the snapshot.</summary>
    Published,

    /// <summary>No provider report survived collection and freshness, so nothing was sent.</summary>
    NothingToPublish,

    /// <summary>Publish failed on auth or a transient problem; the snapshot is queued on disk for a later flush.</summary>
    QueuedForLater,

    /// <summary>The API rejected the snapshot permanently; it was deliberately not queued.</summary>
    RejectedPermanently,

    /// <summary>Collection, freshness or mapping failed, so no snapshot was produced.</summary>
    CollectionFailed,
}

/// <summary>
/// The headless collection loop. Every interval: flush any queued snapshots,
/// fetch every provider in parallel, map onto the wire contract, publish —
/// queueing the document whenever the API cannot take it. No failure path is
/// allowed to escape a cycle: a rogue provider, a dead network, or a full disk
/// is logged and the process keeps running.
/// </summary>
public sealed class AgentHost
{
    private readonly AgentConfig _config;
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly ISnapshotPublisher _publisher;
    private readonly OfflineQueue _queue;
    private readonly SnapshotPipeline _pipeline;

    public AgentHost(
        AgentConfig config,
        IReadOnlyList<IUsageProvider> providers,
        SnapshotMapper mapper,
        ISnapshotPublisher publisher,
        OfflineQueue queue)
    {
        _config = config;
        _providers = providers;
        _publisher = publisher;
        _queue = queue;
        _pipeline = new SnapshotPipeline(providers, mapper, config.StalenessThreshold);
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
        var cycle = Stopwatch.StartNew();

        // Oldest queued data leaves first so the API sees chronology.
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

        var collected = await _pipeline.CollectGateMapAsync(cancellationToken);
        if (collected is null)
        {
            return CycleOutcome.CollectionFailed;
        }

        foreach (var note in collected.Freshness.Notes)
        {
            AgentLog.Info(note);
        }

        foreach (var omission in collected.Freshness.Omissions)
        {
            AgentLog.Warn(omission);
        }

        foreach (var warning in collected.Mapped.Warnings)
        {
            AgentLog.Warn(warning);
        }

        var providerCount = collected.Mapped.Request.Providers?.Count ?? 0;
        if (providerCount == 0)
        {
            // The API requires 1..16 providers, so an all-stale cycle must
            // publish nothing at all rather than an empty document.
            AgentLog.Warn(collected.Freshness.Omissions.Count > 0
                ? $"All {collected.Freshness.Omissions.Count} provider report(s) were omitted as stale; " +
                  "publishing nothing this cycle so a fresher reading from another machine can win the server's newest-first merge."
                : "No provider produced a reportable result this cycle; nothing published.");
            return CycleOutcome.NothingToPublish;
        }

        var result = await _publisher.PublishAsync(collected.Mapped.Json, cancellationToken);
        switch (result.Outcome)
        {
            case PublishOutcome.Success:
                AgentLog.Info($"Published snapshot for {providerCount} provider(s) in {cycle.ElapsedMilliseconds} ms.");
                return CycleOutcome.Published;

            case PublishOutcome.AuthFailure:
                AgentLog.Error($"Authentication failed ({Describe(result)}). Snapshot queued; it will flush once {EnvironmentAccessTokenProvider.VariableName} is accepted.");
                break;

            case PublishOutcome.PermanentFailure:
                AgentLog.Error($"API rejected the snapshot permanently ({Describe(result)}); it will not be retried.");
                return CycleOutcome.RejectedPermanently;

            case PublishOutcome.TransientFailure:
            default:
                AgentLog.Warn($"API unreachable ({Describe(result)}); snapshot queued for a later flush.");
                break;
        }

        try
        {
            if (_queue.Enqueue(collected.Mapped.Json, collected.ObservedAt))
            {
                AgentLog.Info($"Snapshot queued for later upload ({_queue.Count} pending).");
            }
            else
            {
                AgentLog.Info("Identical snapshot already queued; nothing added.");
            }
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Could not queue the snapshot for later upload: {ex.Message}");
        }

        return CycleOutcome.QueuedForLater;
    }

    /// <summary>
    /// Sends queued snapshots oldest-first. Stops at the first transient or
    /// auth failure (entries stay queued for a later cycle) and permanently
    /// discards entries the API rejects outright - e.g. a snapshot older than
    /// the server's accepted observation window can never become valid.
    /// </summary>
    public async Task FlushQueueAsync(CancellationToken cancellationToken)
    {
        var flushed = 0;
        foreach (var path in _queue.EnumerateEntryPaths())
        {
            if (!_queue.TryReadEntry(path, out var document))
            {
                continue;
            }

            var result = await _publisher.PublishAsync(document, cancellationToken);
            switch (result.Outcome)
            {
                case PublishOutcome.Success:
                    _queue.Remove(path);
                    flushed++;
                    break;

                case PublishOutcome.PermanentFailure:
                    AgentLog.Warn($"Discarding a queued snapshot the API will never accept ({Describe(result)}).");
                    _queue.Remove(path);
                    break;

                case PublishOutcome.AuthFailure:
                    if (flushed > 0)
                    {
                        AgentLog.Info($"Flushed {flushed} queued snapshot(s) before authentication failed.");
                    }

                    AgentLog.Error($"Not authenticated ({Describe(result)}) - {_queue.Count} snapshot(s) stay queued.");
                    return;

                case PublishOutcome.TransientFailure:
                default:
                    if (flushed > 0)
                    {
                        AgentLog.Info($"Flushed {flushed} queued snapshot(s) before the API became unreachable.");
                    }

                    AgentLog.Warn($"API unreachable ({Describe(result)}) - {_queue.Count} snapshot(s) stay queued.");
                    return;
            }
        }

        if (flushed > 0)
        {
            AgentLog.Info($"Flushed {flushed} queued snapshot(s).");
        }
    }

    /// <summary>HTTP status plus the (truncated) error detail, for logs only.</summary>
    private static string Describe(PublishResult result)
    {
        var suffix = string.IsNullOrWhiteSpace(result.Detail) ? "" : $" - {result.Detail}";
        return result.StatusCode is { } status ? $"HTTP {status}{suffix}" : suffix.TrimStart(' ', '-');
    }
}
