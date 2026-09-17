using VibeMeter.Agent;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Pins the cycle outcomes that --once translates into its exit code: a
/// successful publish and an all-stale cycle are successes; a snapshot that
/// could only be queued, or was rejected outright, is not. (The outcome-to-
/// exit-code mapping itself is Program.OnceExitCode, pinned in AgentCliTests.)
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

    [Fact]
    public async Task TransientAndAuthFailures_QueueTheSnapshotAndReturnQueuedForLater()
    {
        var queuedBefore = _queue.Count;

        var transientHost = CreateHost(new OutcomePublisher(PublishOutcome.TransientFailure, 503), Live("codex"));

        Assert.Equal(CycleOutcome.QueuedForLater, await transientHost.RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(queuedBefore + 1, _queue.Count);

        var authHost = CreateHost(new OutcomePublisher(PublishOutcome.AuthFailure, 401), Live("codex"));

        Assert.Equal(CycleOutcome.QueuedForLater, await authHost.RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(queuedBefore + 2, _queue.Count);
    }

    [Fact]
    public async Task PermanentRejection_IsNotQueued()
    {
        var host = CreateHost(new OutcomePublisher(PublishOutcome.PermanentFailure, 422), Live("codex"));

        Assert.Equal(CycleOutcome.RejectedPermanently, await host.RunOneCycleAsync(CancellationToken.None));
        Assert.Equal(0, _queue.Count);
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
