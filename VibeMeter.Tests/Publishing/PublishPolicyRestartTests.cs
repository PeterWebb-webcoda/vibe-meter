using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The floor has to survive the host that enforces it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The observation these tests exist for.</b> The tray app wrote four rows at
/// 11:45:46, 11:49:14, 11:49:53 and 11:50:46 — three of them inside two minutes,
/// against a 290-second minimum. Nothing was wrong with
/// <see cref="PublishPolicy.Decide"/>, and nothing bypassed it: the cycle
/// consults the policy before the network and records the result on every path,
/// including a queued one. What was wrong was the STATE the decision was taken
/// against. <c>DesktopPublishHost</c> gave its policy an in-memory store, and
/// <c>App.RestartPublishing</c> disposes and rebuilds the host at startup and on
/// every settings save — so each rebuild handed the policy a blank baseline, it
/// took the "nothing published yet" branch, and the minimum interval was never
/// reached at all.
/// </para>
/// <para>
/// So these tests drive the REBUILD, not just the cycle: a policy built the way
/// the desktop host builds one, thrown away, and built again over the same queue
/// directory.
/// </para>
/// </remarks>
public sealed class PublishPolicyRestartTests : IDisposable
{
    /// <summary>Generous; every wait here is for something that should be immediate.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly PublishPolicyOptions Options = PublishPolicyOptions.Default;
    private static readonly DateTimeOffset LastPublish = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);

    private const string Before = "f1-aaaa";
    private const string After = "f1-bbbb";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // A leftover temp directory on the test machine is harmless.
        }
    }

    // --- The decision, as a pure function -------------------------------------

    [Fact]
    public void ARestartInsideTheMinimumInterval_DoesNotPublish()
    {
        // The whole point of putting the allowance BELOW the floor: a host that
        // is rebuilt on every saved setting must not be able to buy a row by
        // being rebuilt.
        var decision = PublishPolicy.Decide(
            new PublishPolicyState(Before, LastPublish),
            Before,
            LastPublish + Options.MinimumInterval - TimeSpan.FromSeconds(1),
            Options,
            restartPending: true);

        Assert.False(decision.ShouldPublish);
        Assert.Equal(PublishReason.TooSoon, decision.Reason);

        // Not silently swallowed either - the explanation says the startup
        // publish is still coming.
        Assert.Contains("restarted", decision.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARestartAfterTheMinimumInterval_PublishesEvenThoughNothingChanged()
    {
        // A relaunch is worth one row: without this, someone who had just
        // switched publishing on would see silence for up to a heartbeat and
        // reasonably conclude it was broken.
        var now = LastPublish + Options.MinimumInterval + TimeSpan.FromSeconds(1);

        var withRestart = PublishPolicy.Decide(
            new PublishPolicyState(Before, LastPublish), Before, now, Options, restartPending: true);

        Assert.True(withRestart.ShouldPublish);
        Assert.Equal(PublishReason.HostRestarted, withRestart.Reason);
        Assert.False(string.IsNullOrWhiteSpace(withRestart.Explanation));

        // And it is the allowance that does it, not the elapsed time: the same
        // moment without one stays quiet until the heartbeat.
        var withoutRestart = PublishPolicy.Decide(
            new PublishPolicyState(Before, LastPublish), Before, now, Options);

        Assert.False(withoutRestart.ShouldPublish);
        Assert.Equal(PublishReason.Unchanged, withoutRestart.Reason);
    }

    [Fact]
    public void ARestartDoesNotRelabelAGenuineChange()
    {
        // Both publish; the reason an operator reads has to be the informative
        // one, or a log stops distinguishing "the figures moved" from "the app
        // was relaunched".
        var decision = PublishPolicy.Decide(
            new PublishPolicyState(Before, LastPublish),
            After,
            LastPublish + Options.MinimumInterval + TimeSpan.FromSeconds(1),
            Options,
            restartPending: true);

        Assert.True(decision.ShouldPublish);
        Assert.Equal(PublishReason.ContentChanged, decision.Reason);
    }

    [Fact]
    public void TheStartupPublish_IsSpentOnce()
    {
        // Spent by RecordPublished, whichever rule actually let the row through:
        // a host that has said something since it started has nothing left to
        // say about having started.
        var store = new InMemoryPublishPolicyStore();
        store.Write(new PublishPolicyState(Fingerprint(10), LastPublish));
        var policy = new PublishPolicy(store, options: null, publishOnStart: true);

        var first = policy.Evaluate(Snapshot(10), LastPublish + Options.MinimumInterval);
        Assert.True(first.Decision.ShouldPublish);
        Assert.Equal(PublishReason.HostRestarted, first.Decision.Reason);

        policy.RecordPublished(first.Fingerprint, LastPublish + Options.MinimumInterval);

        var second = policy.Evaluate(Snapshot(10), LastPublish + (2 * Options.MinimumInterval));
        Assert.False(second.Decision.ShouldPublish);
        Assert.Equal(PublishReason.Unchanged, second.Decision.Reason);
    }

    [Fact]
    public void TheAgentsPolicy_GetsNoStartupPublish()
    {
        // The default is off, and the agent takes the default: it is restarted
        // by service managers and supervisors, so a crash loop must not become a
        // write loop.
        var store = new InMemoryPublishPolicyStore();
        store.Write(new PublishPolicyState(Fingerprint(10), LastPublish));

        var decision = new PublishPolicy(store)
            .Evaluate(Snapshot(10), LastPublish + Options.MinimumInterval)
            .Decision;

        Assert.False(decision.ShouldPublish);
        Assert.Equal(PublishReason.Unchanged, decision.Reason);
    }

    // --- The rebuild, through the real cycle ----------------------------------

    [Fact]
    public async Task CyclesInsideTheMinimumInterval_AcrossHostRebuilds_WriteExactlyOneRow()
    {
        // The live observation, reproduced: four cycles well inside the
        // 290-second floor, three of them under a freshly built host, must be
        // one row. Before the fix this produced four.
        var queue = Queue("tray");
        var publisher = new RecordingPublisher();

        var first = Cycle(publisher, queue);
        Assert.Equal(CycleOutcome.Published, await first.PublishAsync([Live(10)], CancellationToken.None));
        Assert.Equal(CycleOutcome.SkippedByPolicy, await first.PublishAsync([Live(11)], CancellationToken.None));

        // The settings were saved: App.RestartPublishing disposes the host and
        // builds another over the same queue directory. Twice more, because in
        // production it happened three times inside two minutes.
        Assert.Equal(
            CycleOutcome.SkippedByPolicy,
            await Cycle(publisher, queue).PublishAsync([Live(12)], CancellationToken.None));
        Assert.Equal(
            CycleOutcome.SkippedByPolicy,
            await Cycle(publisher, queue).PublishAsync([Live(13)], CancellationToken.None));

        // One row from four cycles, and every one of those cycles carried
        // DIFFERENT figures: what is being pinned is the floor, not the
        // fingerprint. A provider whose percentage ticks every cycle must not be
        // able to out-run the floor by rebuilding the host.
        Assert.Single(publisher.Documents);
    }

    [Fact]
    public async Task ARebuiltHost_PublishesOnceOnceTheFloorHasElapsed()
    {
        // The other half of the requirement: a restart is a legitimate reason to
        // publish, so a rebuild must not go silent until the heartbeat either.
        var queue = Queue("tray");
        var publisher = new RecordingPublisher();

        Assert.Equal(
            CycleOutcome.Published,
            await Cycle(publisher, queue).PublishAsync([Live(10)], CancellationToken.None));

        // Age the recorded row past the floor without waiting 290 seconds. The
        // fingerprint is kept exactly as the cycle recorded it, so the figures
        // below are genuinely UNCHANGED - only a restart can publish them.
        AgeLastPublish(queue, TimeSpan.FromMinutes(10));

        Assert.Equal(
            CycleOutcome.Published,
            await Cycle(publisher, queue).PublishAsync([Live(10)], CancellationToken.None));
        Assert.Equal(2, publisher.Documents.Count);

        // And that is one row, not a new baseline: rebuilding again straight
        // away is back inside the floor.
        Assert.Equal(
            CycleOutcome.SkippedByPolicy,
            await Cycle(publisher, queue).PublishAsync([Live(10)], CancellationToken.None));
        Assert.Equal(2, publisher.Documents.Count);
    }

    [Fact]
    public async Task AnAgentStyleHost_StaysQuietWhereTheDesktopHostPublishesOnce()
    {
        // Same state, same figures, same elapsed time: the only difference is
        // the startup allowance, which is what the desktop host's rebuild needs
        // and the agent's supervisor-driven restart must not have.
        var queue = Queue("agent");
        var publisher = new RecordingPublisher();

        var agentPolicy = new PublishPolicy(FilePublishPolicyStore.ForQueue(queue));
        Assert.Equal(
            CycleOutcome.Published,
            await Cycle(publisher, queue, agentPolicy).PublishAsync([Live(10)], CancellationToken.None));

        AgeLastPublish(queue, TimeSpan.FromMinutes(10));

        Assert.Equal(
            CycleOutcome.SkippedByPolicy,
            await Cycle(publisher, queue, new PublishPolicy(FilePublishPolicyStore.ForQueue(queue)))
                .PublishAsync([Live(10)], CancellationToken.None));

        Assert.Single(publisher.Documents);
    }

    // --- The rebuild, through the real host ------------------------------------

    [Fact]
    public async Task RebuildingTheDesktopHost_DoesNotResetTheFloor()
    {
        // End to end through DesktopPublishHost itself, which is what
        // App.RestartPublishing disposes and replaces. Each host here is built
        // from the same queue directory the tray app uses for its own.
        var queue = Queue("tray");
        var publisher = new RecordingPublisher();

        Assert.True(await PublishThrough(publisher, queue, [Live(10)]));

        // Two rebuilds inside the floor - the production sequence.
        Assert.True(await PublishThrough(publisher, queue, [Live(11)]));
        Assert.True(await PublishThrough(publisher, queue, [Live(12)]));

        Assert.Single(publisher.Documents);
    }

    // --- Helpers ---------------------------------------------------------------

    /// <summary>Runs one refresh through a freshly built host, as a rebuild would.</summary>
    private async Task<bool> PublishThrough(
        RecordingPublisher publisher,
        OfflineQueue queue,
        IReadOnlyList<ProviderUsage> collected)
    {
        using var host = new DesktopPublishHost(Cycle(publisher, queue), NullPublishLog.Instance);
        var started = host.Publish(collected);
        await host.Current.WaitAsync(Patience);
        return started;
    }

    /// <summary>
    /// Backdates the recorded publish so a test can reach the far side of the
    /// floor without waiting for it, keeping the fingerprint untouched so the
    /// figures still read as unchanged.
    /// </summary>
    private static void AgeLastPublish(OfflineQueue queue, TimeSpan by)
    {
        var store = FilePublishPolicyStore.ForQueue(queue);
        var state = store.Read();
        Assert.NotNull(state.LastPublishedAt);
        store.Write(state with { LastPublishedAt = state.LastPublishedAt!.Value - by });
    }

    private OfflineQueue Queue(string name) => new(Path.Combine(_root, name, "offline-queue"));

    /// <summary>
    /// A cycle wired exactly as the desktop host wires one — including the
    /// policy, which is the production factory rather than a copy of it.
    /// </summary>
    private SnapshotPublishCycle Cycle(ISnapshotPublisher publisher, OfflineQueue queue, PublishPolicy? policy = null) =>
        new(
            new SnapshotComposer(new SnapshotMapper(), TimeSpan.FromMinutes(20), NullPublishLog.Instance),
            publisher,
            queue,
            NullPublishLog.Instance,
            policy ?? DesktopPublishHost.CreatePolicyFor(queue));

    private static MappedSnapshot Snapshot(int percent) =>
        new SnapshotMapper().Map([Live(percent)], LastPublish);

    private static string Fingerprint(int percent) => ContentFingerprint.For(Snapshot(percent));

    private sealed class RecordingPublisher : ISnapshotPublisher
    {
        private readonly List<string> _documents = new();

        public IReadOnlyList<string> Documents
        {
            get
            {
                lock (_documents)
                {
                    return _documents.ToList();
                }
            }
        }

        public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken)
        {
            lock (_documents)
            {
                _documents.Add(document);
            }

            return Task.FromResult(new PublishResult(PublishOutcome.Success, 200, null));
        }
    }

    private static ProviderUsage Live(int percent) => new()
    {
        ProviderId = "codex",
        DisplayName = "codex",
        State = ProviderState.Ok,
        Gauges = [new UsageGauge("g", "Gauge", null, percent, null)],
    };
}
