using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The publish policy as a pure function: given the previous fingerprint, when
/// the last publish happened, this cycle's fingerprint and the time now, should
/// this host write a row? Everything here is decided without a clock, a disk or
/// a network, which is the reason the decision does not live inside the publish
/// cycle.
/// </summary>
public sealed class PublishPolicyDecisionTests
{
    private static readonly DateTimeOffset LastPublish = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);
    private static readonly PublishPolicyOptions Options = PublishPolicyOptions.Default;

    private const string Before = "f1-aaaa";
    private const string After = "f1-bbbb";

    [Fact]
    public void NothingPublishedYet_Publishes()
    {
        var decision = PublishPolicy.Decide(PublishPolicyState.None, After, LastPublish, Options);

        Assert.True(decision.ShouldPublish);
        Assert.Equal(PublishReason.FirstPublish, decision.Reason);
    }

    [Fact]
    public void UnchangedContentInsideTheMinimumInterval_DoesNotPublish()
    {
        var decision = Decide(Before, At(Options.MinimumInterval - TimeSpan.FromSeconds(1)));

        Assert.False(decision.ShouldPublish);
        Assert.Equal(PublishReason.TooSoon, decision.Reason);
    }

    [Fact]
    public void ChangedContentInsideTheMinimumInterval_DoesNotPublish()
    {
        // The rule that actually caps the write rate. A provider whose
        // percentage ticks every single cycle must not be able to buy its way
        // past the floor - the newer figures simply go out on the first cycle
        // after it elapses, so nothing is lost but a few minutes.
        var decision = Decide(After, At(Options.MinimumInterval - TimeSpan.FromSeconds(1)));

        Assert.False(decision.ShouldPublish);
        Assert.Equal(PublishReason.TooSoon, decision.Reason);
    }

    [Fact]
    public void ChangedContentAfterTheMinimumInterval_Publishes()
    {
        var decision = Decide(After, At(Options.MinimumInterval + TimeSpan.FromSeconds(1)));

        Assert.True(decision.ShouldPublish);
        Assert.Equal(PublishReason.ContentChanged, decision.Reason);
    }

    [Fact]
    public void ChangedContentExactlyAtTheMinimumInterval_Publishes()
    {
        // The floor is "no more often THAN", so the instant it is reached is
        // allowed - otherwise a host on a timer tuned to the floor would be
        // pushed out a whole period by nothing but rounding.
        var decision = Decide(After, At(Options.MinimumInterval));

        Assert.True(decision.ShouldPublish);
        Assert.Equal(PublishReason.ContentChanged, decision.Reason);
    }

    [Fact]
    public void UnchangedContentBetweenTheIntervalAndTheHeartbeat_DoesNotPublish()
    {
        var decision = Decide(Before, At(Options.MinimumInterval + TimeSpan.FromSeconds(1)));

        Assert.False(decision.ShouldPublish);
        Assert.Equal(PublishReason.Unchanged, decision.Reason);
    }

    [Fact]
    public void UnchangedContentAfterTheHeartbeat_Publishes()
    {
        // Without this the published timestamp would decay into "when the
        // numbers last moved", and a machine that is switched off would be
        // indistinguishable from one whose quota simply has not budged.
        var decision = Decide(Before, At(Options.HeartbeatInterval));

        Assert.True(decision.ShouldPublish);
        Assert.Equal(PublishReason.Heartbeat, decision.Reason);
    }

    [Fact]
    public void ChangedContentAfterTheHeartbeat_PublishesAsAChange()
    {
        var decision = Decide(After, At(Options.HeartbeatInterval + TimeSpan.FromMinutes(5)));

        Assert.True(decision.ShouldPublish);
        Assert.Equal(PublishReason.ContentChanged, decision.Reason);
    }

    [Fact]
    public void AClockThatWentBackwards_Publishes()
    {
        // Negative elapsed time makes both intervals unreachable until real
        // time catches up, which could silence a host for hours - the exact
        // failure the heartbeat exists to prevent. Publishing once rewrites the
        // recorded time and costs a single row.
        var decision = Decide(Before, LastPublish - TimeSpan.FromHours(2));

        Assert.True(decision.ShouldPublish);
        Assert.Equal(PublishReason.ClockWentBackwards, decision.Reason);
    }

    [Fact]
    public void EveryDecision_ExplainsItself()
    {
        // An operator seeing no rows must be able to tell "deliberately quiet"
        // from "broken" without attaching a debugger.
        PublishDecision[] decisions =
        [
            Decide(Before, At(TimeSpan.FromSeconds(30))),
            Decide(After, At(TimeSpan.FromSeconds(30))),
            Decide(After, At(Options.MinimumInterval)),
            Decide(Before, At(Options.MinimumInterval)),
            Decide(Before, At(Options.HeartbeatInterval)),
        ];

        Assert.All(decisions, decision => Assert.False(string.IsNullOrWhiteSpace(decision.Explanation)));
    }

    [Fact]
    public void AHeartbeatNoLongerThanTheMinimum_IsRejected()
    {
        // It would fall due on the first cycle the floor allows, which is not a
        // heartbeat - it is publishing every permitted cycle with the change
        // check quietly disabled.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PublishPolicyOptions(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)).Validated());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PublishPolicyOptions(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(1)).Validated());

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PublishPolicyOptions(TimeSpan.Zero, TimeSpan.FromMinutes(5)).Validated());
    }

    [Fact]
    public void TheDefaults_BoundTheDailyWriteRate()
    {
        // The numbers the defaults were chosen for: the tray app refreshing
        // every 60 seconds would write 1,440 rows a user a day. The ceiling
        // holds even if something changes on every single cycle; the floor is
        // what an idle machine costs to stay honest.
        var day = TimeSpan.FromDays(1);
        var ceiling = day / PublishPolicyOptions.DefaultMinimumInterval;
        var floor = day / PublishPolicyOptions.DefaultHeartbeatInterval;

        Assert.True(ceiling < 300, $"worst case was {ceiling:0} rows/day");
        Assert.True(floor < 70, $"idle floor was {floor:0} rows/day");
        Assert.True(PublishPolicyOptions.DefaultMinimumInterval > TimeSpan.FromSeconds(60));
        Assert.True(PublishPolicyOptions.DefaultHeartbeatInterval > PublishPolicyOptions.DefaultMinimumInterval);
    }

    private static DateTimeOffset At(TimeSpan sinceLastPublish) => LastPublish + sinceLastPublish;

    private static PublishDecision Decide(string currentFingerprint, DateTimeOffset now) =>
        PublishPolicy.Decide(new PublishPolicyState(Before, LastPublish), currentFingerprint, now, Options);
}

/// <summary>
/// Where the policy's two remembered values live. The headless agent has to
/// survive a service restart; nobody has to acquire a file to manage.
/// </summary>
public sealed class PublishPolicyStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"));

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
    public void AnUnusedStore_ReportsNothingPublished()
    {
        Assert.Equal(PublishPolicyState.None, new InMemoryPublishPolicyStore().Read());
        Assert.Equal(PublishPolicyState.None, new FilePublishPolicyStore(_directory).Read());
    }

    [Fact]
    public void TheFileStore_SurvivesAProcessRestart()
    {
        var at = new DateTimeOffset(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);
        new FilePublishPolicyStore(_directory).Write(new PublishPolicyState("f1-abc", at));

        // A second instance stands in for the next run of the service: without
        // this, a restarting agent would publish afresh every time it came up.
        var reopened = new FilePublishPolicyStore(_directory).Read();

        Assert.Equal("f1-abc", reopened.Fingerprint);
        Assert.Equal(at, reopened.LastPublishedAt);
    }

    [Fact]
    public void TheInMemoryStore_DoesNot()
    {
        var store = new InMemoryPublishPolicyStore();
        store.Write(new PublishPolicyState("f1-abc", DateTimeOffset.UtcNow));

        // Stated as a test because it is a choice, not an omission: an
        // interactive host that the user has just relaunched should publish.
        Assert.Equal(PublishPolicyState.None, new InMemoryPublishPolicyStore().Read());
        Assert.NotEqual(PublishPolicyState.None, store.Read());
    }

    [Fact]
    public void AnUnreadableStateFile_ReadsAsNothingPublished()
    {
        // Fail open: bookkeeping must never take down a publish cycle, and
        // "publish" is always the safe answer - it costs one row.
        var store = new FilePublishPolicyStore(_directory);
        File.WriteAllText(store.FilePath, "{ this is not the state file you are looking for");

        Assert.Equal(PublishPolicyState.None, store.Read());
    }

    [Fact]
    public void TheStateFile_IsNotMistakenForAQueuedSnapshot()
    {
        // The state lives in the offline-queue directory, and the queue treats
        // every *.json file there as a snapshot to POST. If this ever fails,
        // the policy's bookkeeping is being sent to the collection API.
        var queue = new OfflineQueue(_directory);
        var store = FilePublishPolicyStore.ForQueue(queue);
        store.Write(new PublishPolicyState("f1-abc", DateTimeOffset.UtcNow));

        Assert.True(File.Exists(store.FilePath));
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.EnumerateEntryPaths());
    }
}
