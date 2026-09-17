using System.Diagnostics;
using VibeMeter.Agent.AccessToken;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;

namespace VibeMeter.Agent;

/// <summary>
/// The headless collection loop. Every interval: flush any queued snapshots,
/// fetch every provider in parallel, map onto the wire contract, publish —
/// queueing the document whenever the API cannot take it. No failure path is
/// allowed to escape a cycle: a rogue provider, a dead network, or a full disk
/// is logged and the process keeps running.
/// </summary>
public sealed class AgentHost
{
    // A provider that neither returns nor throws within this window is reported
    // as an error for the cycle, keeping the interval honest. (IUsageProvider.
    // FetchAsync predates the agent and takes no CancellationToken.)
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    private readonly AgentConfig _config;
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly SnapshotMapper _mapper;
    private readonly ISnapshotPublisher _publisher;
    private readonly OfflineQueue _queue;
    private readonly FreshnessGate _freshnessGate;

    public AgentHost(
        AgentConfig config,
        IReadOnlyList<IUsageProvider> providers,
        SnapshotMapper mapper,
        ISnapshotPublisher publisher,
        OfflineQueue queue)
    {
        _config = config;
        _providers = providers;
        _mapper = mapper;
        _publisher = publisher;
        _queue = queue;
        _freshnessGate = new FreshnessGate(config.StalenessThreshold);
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

    /// <summary>One collect-gate-map-publish pass. Internal so tests can run a
    /// single deterministic cycle instead of racing the periodic loop.</summary>
    internal async Task RunOneCycleAsync(CancellationToken cancellationToken)
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
            return;
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
            return;
        }

        foreach (var note in freshness.Notes)
        {
            AgentLog.Info(note);
        }

        foreach (var omission in freshness.Omissions)
        {
            AgentLog.Warn(omission);
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
            return;
        }

        foreach (var warning in mapped.Warnings)
        {
            AgentLog.Warn(warning);
        }

        var providerCount = mapped.Request.Providers?.Count ?? 0;
        if (providerCount == 0)
        {
            // The API requires 1..16 providers, so an all-stale cycle must
            // publish nothing at all rather than an empty document.
            AgentLog.Warn(freshness.Omissions.Count > 0
                ? $"All {freshness.Omissions.Count} provider report(s) were omitted as stale; " +
                  "publishing nothing this cycle so a fresher reading from another machine can win the server's newest-first merge."
                : "No provider produced a reportable result this cycle; nothing published.");
            return;
        }

        var result = await _publisher.PublishAsync(mapped.Json, cancellationToken);
        switch (result.Outcome)
        {
            case PublishOutcome.Success:
                AgentLog.Info($"Published snapshot for {providerCount} provider(s) in {cycle.ElapsedMilliseconds} ms.");
                return;

            case PublishOutcome.AuthFailure:
                AgentLog.Error($"Authentication failed ({Describe(result)}). Snapshot queued; it will flush once {EnvironmentAccessTokenProvider.VariableName} is accepted.");
                break;

            case PublishOutcome.PermanentFailure:
                AgentLog.Error($"API rejected the snapshot permanently ({Describe(result)}); it will not be retried.");
                return;

            case PublishOutcome.TransientFailure:
            default:
                AgentLog.Warn($"API unreachable ({Describe(result)}); snapshot queued for a later flush.");
                break;
        }

        try
        {
            if (_queue.Enqueue(mapped.Json, observedAt))
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

    /// <summary>HTTP status plus the (truncated) error detail, for logs only.</summary>
    private static string Describe(PublishResult result)
    {
        var suffix = string.IsNullOrWhiteSpace(result.Detail) ? "" : $" - {result.Detail}";
        return result.StatusCode is { } status ? $"HTTP {status}{suffix}" : suffix.TrimStart(' ', '-');
    }
}
