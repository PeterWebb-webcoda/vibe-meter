using System.Diagnostics;
using VibeMeter.Core;
using VibeMeter.Publishing.AccessToken;

namespace VibeMeter.Publishing;

/// <summary>
/// What became of one cycle's snapshot — the values the agent's <c>--once</c>
/// translates into a process exit code.
/// </summary>
public enum CycleOutcome
{
    /// <summary>The API accepted the snapshot.</summary>
    Published,

    /// <summary>No provider report survived collection and freshness, so nothing was sent.</summary>
    NothingToPublish,

    /// <summary>Publish failed on auth or a transient problem; the snapshot is queued on disk for a later flush.</summary>
    QueuedForLater,

    /// <summary>
    /// The API refused the snapshot outright rather than merely failing to take
    /// it. The snapshot is still queued — on a bounded budget — in case the
    /// refusal is the API's and not ours, but the cycle reports the rejection
    /// distinctly because it is the outcome worth investigating.
    /// </summary>
    RejectedPermanently,

    /// <summary>Collection, freshness or mapping failed, so no snapshot was produced.</summary>
    CollectionFailed,

    /// <summary>
    /// A snapshot was composed, but <see cref="PublishPolicy"/> judged it not
    /// worth a row: either nothing had changed since the last publish and the
    /// heartbeat was not due, or the minimum interval had not elapsed. A
    /// success, not a failure — the figures are not lost, merely not repeated.
    /// </summary>
    SkippedByPolicy,
}

/// <summary>
/// The publish half of a cycle: freshness gate, map, publish, queue — plus the
/// flush that empties the queue again. It takes reports that have ALREADY been
/// collected, so each host may collect them its own way: the headless agent
/// fetches every provider in parallel on a timer, while the tray app updates
/// each card as its own fetch completes and only fetches the providers the user
/// enabled. Whatever produced the reports, this is the half that decides what
/// reaches the collection API, and no failure path here is allowed to escape:
/// a dead network or a full disk is logged and the caller carries on.
/// A host given a <see cref="PublishPolicy"/> also gets "is this snapshot worth
/// a row?" between composing and publishing — the rule itself lives in the
/// policy, not here.
/// </summary>
public sealed class SnapshotPublishCycle
{
    private readonly SnapshotComposer _composer;
    private readonly ISnapshotPublisher _publisher;
    private readonly OfflineQueue _queue;
    private readonly IPublishLog _log;
    private readonly PublishPolicy? _policy;

    /// <param name="policy">
    /// How often this host is allowed to write a row, or <see langword="null"/>
    /// to publish every cycle. Null is the default because the cost of
    /// publishing every cycle is entirely a function of how often the HOST
    /// cycles: a host on a five-minute timer is already frugal, while one that
    /// refreshes every 60 seconds for the sake of its UI needs the policy or it
    /// writes about 1,440 near-identical rows a user a day. The decision the
    /// policy applies lives in <see cref="PublishPolicy.Decide"/>, not here.
    /// </param>
    public SnapshotPublishCycle(
        SnapshotComposer composer,
        ISnapshotPublisher publisher,
        OfflineQueue queue,
        IPublishLog log,
        PublishPolicy? policy = null)
    {
        _composer = composer;
        _publisher = publisher;
        _queue = queue;
        _log = log;
        _policy = policy;
    }

    /// <summary>
    /// Gates, maps, publishes and — when the API cannot take it — queues one
    /// cycle's already-collected reports. Returns what became of the snapshot
    /// so a caller can turn it into an exit code or a status line.
    /// </summary>
    public async Task<CycleOutcome> PublishAsync(
        IReadOnlyList<ProviderUsage> collected,
        CancellationToken cancellationToken)
    {
        var cycle = Stopwatch.StartNew();

        var composed = _composer.Compose(collected);
        if (composed is null)
        {
            return CycleOutcome.CollectionFailed;
        }

        foreach (var note in composed.Freshness.Notes)
        {
            _log.Info(note);
        }

        foreach (var omission in composed.Freshness.Omissions)
        {
            _log.Warn(omission);
        }

        foreach (var warning in composed.Mapped.Warnings)
        {
            _log.Warn(warning);
        }

        var providerCount = composed.Mapped.Request.Providers?.Count ?? 0;
        if (providerCount == 0)
        {
            // The API requires 1..16 providers, so an all-stale cycle must
            // publish nothing at all rather than an empty document.
            _log.Warn(composed.Freshness.Omissions.Count > 0
                ? $"All {composed.Freshness.Omissions.Count} provider report(s) were omitted as stale; " +
                  "publishing nothing this cycle so a fresher reading from another machine can win the server's newest-first merge."
                : "No provider produced a reportable result this cycle; nothing published.");
            return CycleOutcome.NothingToPublish;
        }

        // Worth a row at all? Asked AFTER composing, because the answer depends
        // on the mapped document - what the freshness gate omitted is part of
        // what changed - and BEFORE anything reaches the network or the queue.
        // The composer's observedAt is the cycle's single "now", so the same
        // instant judges the intervals and stamps the document.
        string? fingerprint = null;
        if (_policy is not null)
        {
            var (decision, current) = _policy.Evaluate(composed.Mapped, composed.ObservedAt);
            if (!decision.ShouldPublish)
            {
                _log.Info(decision.Explanation);
                return CycleOutcome.SkippedByPolicy;
            }

            fingerprint = current;

            // A publish already announces itself below, so only the reasons an
            // operator could not otherwise infer are worth a second line: a
            // heartbeat and a restart (a row with nothing new in it looks like a
            // bug until you know why it is there) and a clock that ran
            // backwards.
            if (decision.Reason is PublishReason.Heartbeat
                or PublishReason.HostRestarted
                or PublishReason.ClockWentBackwards)
            {
                _log.Info(decision.Explanation);
            }
        }

        var result = await _publisher.PublishAsync(composed.Mapped.Json, cancellationToken);

        // Recorded whatever became of the attempt: a queued snapshot is still
        // delivered by a later flush, so re-sending these same figures next
        // cycle under a fresh timestamp would put in a second row for one
        // reading. See PublishPolicy.RecordPublished.
        if (_policy is not null && fingerprint is not null)
        {
            _policy.RecordPublished(fingerprint, composed.ObservedAt);
        }

        var rejected = false;
        switch (result.Outcome)
        {
            case PublishOutcome.Success:
                _log.Info($"Published snapshot for {providerCount} provider(s) in {cycle.ElapsedMilliseconds} ms.");
                return CycleOutcome.Published;

            case PublishOutcome.AuthFailure:
                _log.Error($"Authentication failed ({Describe(result)}). Snapshot queued; it will flush once {EnvironmentAccessTokenProvider.VariableName} is accepted.");
                break;

            case PublishOutcome.PermanentFailure:
                // "The API refused this document" is not the same fact as "this
                // document is wrong". The host cannot tell the two apart from a
                // 4xx: an API rolled back behind a client that already emits the
                // newer schema rejects perfectly good payloads exactly this way.
                // Discarding here made a rollback cost data permanently, so the
                // snapshot is queued like any other failure and re-offered on a
                // bounded budget (OfflineQueue.MaxRejections) — a rollback then
                // costs latency, and only a payload that stays refused for the
                // whole budget is finally given up on.
                _log.Error(
                    $"API rejected the snapshot ({Describe(result)}); queueing it in case the API, not the payload, " +
                    $"is what is wrong - it will be re-offered up to {OfflineQueue.MaxRejections} times before being discarded.");
                rejected = true;
                break;

            case PublishOutcome.TransientFailure:
            default:
                _log.Warn($"API unreachable ({Describe(result)}); snapshot queued for a later flush.");
                break;
        }

        try
        {
            if (_queue.Enqueue(composed.Mapped.Json, composed.ObservedAt))
            {
                _log.Info($"Snapshot queued for later upload ({_queue.Count} pending).");
            }
            else
            {
                _log.Info("Identical snapshot already queued; nothing added.");
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Could not queue the snapshot for later upload: {ex.Message}");
        }

        return rejected ? CycleOutcome.RejectedPermanently : CycleOutcome.QueuedForLater;
    }

    /// <summary>
    /// Sends queued snapshots oldest-first. Stops at the first transient or
    /// auth failure (entries stay queued for a later cycle). An entry the API
    /// refuses outright is kept and re-offered next cycle, up to
    /// <see cref="OfflineQueue.MaxRejections"/> times: the refusal may be the
    /// API's (a rollback validating against an older schema) rather than the
    /// document's, and the host cannot tell which from a 4xx. Only once that
    /// budget is spent is the entry discarded - a snapshot older than the
    /// server's accepted observation window, say, never does become valid.
    /// One refused entry never blocks the rest: the flush moves on to the next.
    /// </summary>
    public async Task FlushQueueAsync(CancellationToken cancellationToken)
    {
        var flushed = 0;
        var rejected = 0;
        var discarded = 0;
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
                    if (_queue.RecordRejection(path))
                    {
                        rejected++;
                    }
                    else
                    {
                        _log.Warn(
                            $"Discarded a queued snapshot the API has now refused {OfflineQueue.MaxRejections} times " +
                            $"({Describe(result)}); it is being treated as unacceptable rather than retried forever.");
                        discarded++;
                    }

                    break;

                case PublishOutcome.AuthFailure:
                    if (flushed > 0)
                    {
                        _log.Info($"Flushed {flushed} queued snapshot(s) before authentication failed.");
                    }

                    ReportRejections();
                    _log.Error($"Not authenticated ({Describe(result)}) - {_queue.Count} snapshot(s) stay queued.");
                    return;

                case PublishOutcome.TransientFailure:
                default:
                    if (flushed > 0)
                    {
                        _log.Info($"Flushed {flushed} queued snapshot(s) before the API became unreachable.");
                    }

                    ReportRejections();
                    _log.Warn($"API unreachable ({Describe(result)}) - {_queue.Count} snapshot(s) stay queued.");
                    return;
            }
        }

        if (flushed > 0)
        {
            _log.Info($"Flushed {flushed} queued snapshot(s).");
        }

        ReportRejections();
        return;

        // One line per flush rather than one per entry: while an API is rolled
        // back every queued snapshot is refused every cycle, and a per-entry
        // warning would bury the fact in its own noise.
        void ReportRejections()
        {
            if (rejected > 0)
            {
                _log.Warn(
                    $"{rejected} queued snapshot(s) were refused again and kept for a later attempt; " +
                    $"{discarded} reached the {OfflineQueue.MaxRejections}-rejection limit and were discarded.");
            }
            else if (discarded > 0)
            {
                _log.Warn($"{discarded} queued snapshot(s) reached the {OfflineQueue.MaxRejections}-rejection limit and were discarded.");
            }

            rejected = 0;
            discarded = 0;
        }
    }

    /// <summary>HTTP status plus the (truncated) error detail, for logs only.</summary>
    private static string Describe(PublishResult result)
    {
        var suffix = string.IsNullOrWhiteSpace(result.Detail) ? "" : $" - {result.Detail}";
        return result.StatusCode is { } status ? $"HTTP {status}{suffix}" : suffix.TrimStart(' ', '-');
    }
}
