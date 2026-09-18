using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The publish cycle's half of the policy: consult it after composing and
/// before the network, record what was sent, and leave every other path exactly
/// as it was. A host that passes no policy must behave as it always has.
/// </summary>
public sealed class PublishPolicyCycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"));

    private readonly OfflineQueue _queue;

    public PublishPolicyCycleTests()
    {
        _queue = new OfflineQueue(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory on the test machine is harmless.
        }
    }

    [Fact]
    public async Task WithAPolicy_ASecondCycleOfTheSameFiguresWritesNoRow()
    {
        var publisher = new RecordingPublisher(PublishOutcome.Success);
        var cycle = CreateCycle(publisher, new PublishPolicy(new InMemoryPublishPolicyStore()));

        Assert.Equal(CycleOutcome.Published, await cycle.PublishAsync([Live("codex")], CancellationToken.None));
        Assert.Equal(CycleOutcome.SkippedByPolicy, await cycle.PublishAsync([Live("codex")], CancellationToken.None));

        Assert.Single(publisher.Documents);
    }

    [Fact]
    public async Task WithoutAPolicy_EveryCyclePublishes()
    {
        // The default is unchanged behaviour: a host on a five-minute timer is
        // already frugal and must not silently start skipping.
        var publisher = new RecordingPublisher(PublishOutcome.Success);
        var cycle = CreateCycle(publisher, policy: null);

        Assert.Equal(CycleOutcome.Published, await cycle.PublishAsync([Live("codex")], CancellationToken.None));
        Assert.Equal(CycleOutcome.Published, await cycle.PublishAsync([Live("codex")], CancellationToken.None));

        Assert.Equal(2, publisher.Documents.Count);
    }

    [Fact]
    public async Task AQueuedSnapshot_CountsAsPublished()
    {
        // A queued document is delivered by a later flush, so repeating the
        // same figures next cycle under a fresh timestamp would put in a second
        // row for one reading.
        var publisher = new RecordingPublisher(PublishOutcome.TransientFailure);
        var cycle = CreateCycle(publisher, new PublishPolicy(new InMemoryPublishPolicyStore()));

        Assert.Equal(CycleOutcome.QueuedForLater, await cycle.PublishAsync([Live("codex")], CancellationToken.None));
        Assert.Equal(CycleOutcome.SkippedByPolicy, await cycle.PublishAsync([Live("codex")], CancellationToken.None));

        Assert.Single(publisher.Documents);
        Assert.Equal(1, _queue.Count);
    }

    [Fact]
    public async Task AllStaleCycles_NeverReachThePolicy()
    {
        // Nothing was composed, so there is nothing to compare and nothing to
        // record - the next cycle with real figures must still publish.
        var publisher = new RecordingPublisher(PublishOutcome.Success);
        var cycle = CreateCycle(publisher, new PublishPolicy(new InMemoryPublishPolicyStore()));

        var stale = FileDerived("claude", DateTimeOffset.UtcNow.AddHours(-3));

        Assert.Equal(CycleOutcome.NothingToPublish, await cycle.PublishAsync([stale], CancellationToken.None));
        Assert.Equal(CycleOutcome.Published, await cycle.PublishAsync([Live("codex")], CancellationToken.None));

        Assert.Single(publisher.Documents);
    }

    private SnapshotPublishCycle CreateCycle(ISnapshotPublisher publisher, PublishPolicy? policy) => new(
        new SnapshotComposer(new SnapshotMapper(), TimeSpan.FromMinutes(20), NullPublishLog.Instance),
        publisher,
        _queue,
        NullPublishLog.Instance,
        policy);

    private sealed class RecordingPublisher(PublishOutcome outcome) : ISnapshotPublisher
    {
        public List<string> Documents { get; } = new();

        public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken)
        {
            Documents.Add(document);
            return Task.FromResult(new PublishResult(
                outcome,
                outcome == PublishOutcome.Success ? 200 : 503,
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
