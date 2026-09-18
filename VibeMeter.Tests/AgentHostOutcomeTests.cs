using VibeMeter.Agent;
using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Pins the cycle outcomes that --once translates into its exit code: a
/// successful publish and an all-stale cycle are successes; a snapshot that
/// could only be queued, or was rejected outright, is not. (The outcome-to-
/// exit-code mapping itself is Program.OnceExitCode, pinned in AgentCliTests.)
/// Also pins what happens to the snapshot itself on each of those paths -
/// above all that a rejection retains it, on a bounded budget, instead of
/// destroying it.
/// </summary>
public sealed class AgentHostOutcomeTests : IDisposable
{
    private static readonly Uri ApiBaseUrl = new("https://api.example.com");

    private readonly string _queueDirectory = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"));

    private readonly RecordingPublisher _publisher = new();
    private readonly OfflineQueue _queue;

    public AgentHostOutcomeTests()
    {
        _queue = new OfflineQueue(_queueDirectory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_queueDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory on the test machine is harmless.
        }
    }

    [Fact]
    public async Task SuccessfulPublish_ReturnsPublished()
    {
        var host = CreateHost(Live("codex"));

        Assert.Equal(CycleOutcome.Published, await host.RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task AllStale_ReturnsNothingToPublish()
    {
        var host = CreateHost(FileDerived("claude", observedAt: DateTimeOffset.UtcNow.AddHours(-3)));

        Assert.Equal(CycleOutcome.NothingToPublish, await host.RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(0, _queue.Count);
    }

    // One host per failure mode, each with its own queue directory (xUnit builds
    // a fresh instance per case), rather than two hosts running seconds apart in
    // one directory: the agent's publish policy keeps its state beside the queue,
    // and two cycles of identical figures seconds apart is exactly what it is
    // there to suppress. The claim under test is unchanged - a snapshot the API
    // could not take is queued, and the cycle says so.
    [Theory]
    [InlineData(PublishOutcome.TransientFailure, 503)]
    [InlineData(PublishOutcome.AuthFailure, 401)]
    public async Task AFailureToSend_QueuesTheSnapshotAndReturnsQueuedForLater(PublishOutcome outcome, int statusCode)
    {
        var host = CreateHost(new OutcomePublisher(outcome, statusCode), Live("codex"));

        Assert.Equal(CycleOutcome.QueuedForLater, await host.RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task IdenticalFigures_WriteOneRow_EvenAcrossARestart()
    {
        // The agent's reason for a file-backed policy state: a service manager,
        // a supervisor after a crash, or a scheduled --once restarts this
        // process on its own schedule, and a host that forgot its baseline each
        // time would publish on every restart - out-writing the very loop the
        // policy slows down.
        Assert.Equal(CycleOutcome.Published, await CreateHost(Live("codex")).RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(CycleOutcome.SkippedByPolicy, await CreateHost(Live("codex")).RunOneCycleAsync(CancellationToken.None));

        // A brand-new host over the same queue directory stands in for the next
        // run of the service.
        var restarted = CreateHost(Live("codex"));

        Assert.Equal(CycleOutcome.SkippedByPolicy, await restarted.RunOneCycleAsync(CancellationToken.None));
        Assert.Single(_publisher.Documents);
    }

    [Fact]
    public async Task PermanentRejection_IsStillQueued_SoARollbackCostsLatencyNotData()
    {
        // The regression: the cycle used to return before the enqueue block, so
        // an API that was merely BEHIND the client - rolled back, validating a
        // newer payload against an older schema - silently destroyed every
        // snapshot it rejected. The outcome stays distinct (it is the one worth
        // investigating) but the document survives.
        var host = CreateHost(new OutcomePublisher(PublishOutcome.PermanentFailure, 422), Live("codex"));

        Assert.Equal(CycleOutcome.RejectedPermanently, await host.RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(1, _queue.Count);

        // Queued with a full budget: the cycle's own refusal is the reason it
        // is here, not the first strike against it.
        Assert.Equal(0, OfflineQueue.RejectionsOf(_queue.EnumerateEntryPaths()[0]));
    }

    [Fact]
    public async Task ARepeatedlyRejectedSnapshot_SurvivesTheFlush_UntilItsBudgetIsSpent()
    {
        var host = CreateHost(new OutcomePublisher(PublishOutcome.PermanentFailure, 400), Live("codex"));
        _queue.Enqueue("""{"providers":[{"providerId":"codex"}]}""", DateTimeOffset.UtcNow);

        for (var flush = 1; flush < OfflineQueue.MaxRejections; flush++)
        {
            await host.FlushQueueAsync(CancellationToken.None);

            Assert.Equal(1, _queue.Count);
            Assert.Equal(flush, OfflineQueue.RejectionsOf(_queue.EnumerateEntryPaths()[0]));
        }

        // ...and the budget really is finite: a document the API never accepts
        // is given up on rather than re-sent forever.
        await host.FlushQueueAsync(CancellationToken.None);

        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task ARejectedEntry_DoesNotBlockTheEntriesBehindIt()
    {
        var publisher = new ScriptedPublisher(PublishOutcome.PermanentFailure, PublishOutcome.Success);
        var host = CreateHost(publisher, Live("codex"));
        _queue.Enqueue("refused", DateTimeOffset.UtcNow.AddMinutes(-2));
        _queue.Enqueue("accepted", DateTimeOffset.UtcNow.AddMinutes(-1));

        await host.FlushQueueAsync(CancellationToken.None);

        Assert.Equal(["refused", "accepted"], publisher.Documents);

        var remaining = Assert.Single(_queue.EnumerateEntryPaths());
        Assert.True(_queue.TryReadEntry(remaining, out var document));
        Assert.Equal("refused", document);
        Assert.Equal(1, OfflineQueue.RejectionsOf(remaining));
    }

    private AgentHost CreateHost(params ProviderUsage[] results) => CreateHost(_publisher, results);

    private AgentHost CreateHost(ISnapshotPublisher publisher, params ProviderUsage[] results) => new(
        new AgentConfig(ApiBaseUrl, TimeSpan.FromMinutes(5), _queueDirectory, AgentConfig.DefaultStalenessThreshold),
        results.Select(usage => new StubProvider(usage)).Cast<IUsageProvider>().ToArray(),
        new SnapshotMapper(),
        publisher,
        _queue);

    /// <summary>A provider that always reports the same prepared result. Its id
    /// comes from the report; tests give each stub a distinct id.</summary>
    private sealed class StubProvider(ProviderUsage result) : IUsageProvider
    {
        public string Id => result.ProviderId;
        public string DisplayName => result.DisplayName;
        public Task<ProviderUsage> FetchAsync() => Task.FromResult(result);
    }

    private sealed class RecordingPublisher : ISnapshotPublisher
    {
        public List<string> Documents { get; } = new();

        public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken)
        {
            Documents.Add(document);
            return Task.FromResult(new PublishResult(PublishOutcome.Success, 200, null));
        }
    }

    /// <summary>A publisher that fails every attempt the same way.</summary>
    private sealed class OutcomePublisher(PublishOutcome outcome, int? statusCode) : ISnapshotPublisher
    {
        public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken) =>
            Task.FromResult(new PublishResult(outcome, statusCode, "stub"));
    }

    /// <summary>A publisher that walks a script of outcomes, repeating the last
    /// one once the script runs dry, and records what it was handed.</summary>
    private sealed class ScriptedPublisher(params PublishOutcome[] script) : ISnapshotPublisher
    {
        private int _index;

        public List<string> Documents { get; } = new();

        public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken)
        {
            Documents.Add(document);
            var outcome = script[Math.Min(_index++, script.Length - 1)];
            return Task.FromResult(new PublishResult(
                outcome,
                outcome == PublishOutcome.Success ? 200 : 400,
                "stub"));
        }
    }

    private static ProviderUsage Live(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };

    private static ProviderUsage FileDerived(string id, DateTimeOffset observedAt) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        SourceObservedAt = observedAt,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };
}
