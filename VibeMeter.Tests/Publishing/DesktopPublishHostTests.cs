using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The rules that keep publishing off the tray app's dispatcher. Its refresh
/// runs on the UI thread from an async-void timer Tick and captures the
/// dispatcher on every continuation, so a publish that were awaited there would
/// freeze the window for the length of three HTTP attempts, do the offline
/// queue's file writes on the dispatcher, and turn any escaping exception into
/// an unhandled one that ends the process. Each test below pins one half of
/// that: the work is handed over and NOT waited for, and nothing that happens
/// to it comes back.
/// </summary>
public sealed class DesktopPublishHostTests : IDisposable
{
    /// <summary>Generous: every wait here is for something that should be immediate, and a
    /// deadlocked test is more useful as a fast failure than as a hung run.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

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

    [Fact]
    public async Task TheCollectedListReachesThePublishCycle()
    {
        var publisher = new GatedPublisher();
        using var host = CreateHost(publisher);

        Assert.True(host.Publish([Live("codex"), Live("claude")]));
        publisher.Release();
        await host.Current.WaitAsync(Patience);

        var document = Assert.Single(publisher.Documents);
        Assert.Contains("\"providerId\":\"codex\"", document);
        Assert.Contains("\"providerId\":\"claude\"", document);
    }

    [Fact]
    public async Task Publish_ReturnsWhileTheCycleIsStillRunning()
    {
        // The claim the refresh depends on: the call is a hand-off, not a wait.
        var publisher = new GatedPublisher();
        using var host = CreateHost(publisher);

        Assert.True(host.Publish([Live("codex")]));

        // Control came back to the caller with the publish demonstrably still in
        // flight - it has reached the publisher and is sitting inside it.
        await publisher.Entered.Task.WaitAsync(Patience);
        Assert.False(host.Current.IsCompleted);

        publisher.Release();
        await host.Current.WaitAsync(Patience);
    }

    [Fact]
    public async Task APublishThatThrows_NeverReachesTheCaller()
    {
        // A failure here would otherwise travel up through the async-void Tick
        // handler, where an unhandled exception takes the whole app down.
        var log = new RecordingLog();
        using var host = CreateHost(new ThrowingPublisher(), log);

        // Neither the call nor the task it started ever faults.
        Assert.True(host.Publish([Live("codex")]));
        await host.Current.WaitAsync(Patience);

        Assert.Equal(TaskStatus.RanToCompletion, host.Current.Status);
        Assert.Contains(log.Errors, message => message.Contains("the publisher exploded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASecondRefresh_CannotPublishWhileTheFirstIsStillInFlight()
    {
        // A publish can outlast several refreshes, and two overlapping ones would
        // race the offline queue and the publish policy. The second is declined
        // rather than queued: its figures are already staler than the ones the
        // next refresh will bring.
        var publisher = new GatedPublisher();
        using var host = CreateHost(publisher);

        Assert.True(host.Publish([Live("codex")]));
        await publisher.Entered.Task.WaitAsync(Patience);

        Assert.False(host.Publish([Live("codex")]));

        publisher.Release();
        await host.Current.WaitAsync(Patience);

        Assert.Single(publisher.Documents);
    }

    [Fact]
    public async Task AfterDispose_NothingMoreIsPublished()
    {
        // Dispose is what app exit calls: an in-flight publish is cancelled and
        // no later refresh can start another one.
        var publisher = new GatedPublisher();
        var host = CreateHost(publisher);

        host.Dispose();

        Assert.False(host.Publish([Live("codex")]));
        await Task.Yield();
        Assert.Empty(publisher.Documents);
    }

    [Fact]
    public void WhenTheSettingIsOff_NothingIsBuiltAndNothingIsWrittenToDisk()
    {
        // The opt-in is checked before anything is constructed, so a machine that
        // is not publishing is left with no offline queue, no token cache and no
        // HTTP client - not merely with a host that declines to send.
        var queueDirectory = Path.Combine(_root, "offline-queue");
        var cacheDirectory = Path.Combine(_root, "token-cache");
        var log = new RecordingLog();

        var host = DesktopPublishHost.TryCreate(
            new DesktopPublishSettings(
                Enabled: false,
                ApiBaseUrl: "https://api.example.com",
                ClientId: "00000000-0000-0000-0000-000000000001",
                TenantId: "00000000-0000-0000-0000-000000000002",
                Scope: "api://vibemeter/Usage.Write"),
            queueDirectory,
            cacheDirectory,
            log,
            _ => { });

        Assert.Null(host);
        Assert.False(Directory.Exists(queueDirectory));
        Assert.False(Directory.Exists(cacheDirectory));

        // Switched off is not a fault, so it is not reported as one.
        Assert.Empty(log.Errors);
        Assert.Empty(log.Warnings);
    }

    [Fact]
    public void WhenTheSettingIsOnButUnconfigured_NothingIsBuiltAndEveryProblemIsReportedAtOnce()
    {
        // A feature that silently does nothing is indistinguishable from a broken
        // one, and a person filling in four fields should not have to save four
        // times to find four mistakes.
        var queueDirectory = Path.Combine(_root, "offline-queue");
        var log = new RecordingLog();

        var host = DesktopPublishHost.TryCreate(
            new DesktopPublishSettings(Enabled: true, ApiBaseUrl: "not-a-url", ClientId: "", TenantId: "", Scope: ""),
            queueDirectory,
            Path.Combine(_root, "token-cache"),
            log,
            _ => { });

        Assert.Null(host);
        Assert.False(Directory.Exists(queueDirectory));

        var reported = Assert.Single(log.Errors);
        Assert.Contains("base URL", reported, StringComparison.Ordinal);
        Assert.Contains("client) id", reported, StringComparison.Ordinal);
        Assert.Contains("tenant) id", reported, StringComparison.Ordinal);
        Assert.Contains("scope", reported, StringComparison.Ordinal);
    }

    private DesktopPublishHost CreateHost(ISnapshotPublisher publisher, IPublishLog? log = null)
    {
        log ??= NullPublishLog.Instance;
        return new DesktopPublishHost(
            new SnapshotPublishCycle(
                new SnapshotComposer(new SnapshotMapper(), TimeSpan.FromMinutes(20), log),
                publisher,
                new OfflineQueue(Path.Combine(_root, "offline-queue")),
                log),
            log);
    }

    /// <summary>
    /// Blocks inside the publish until released, so a test can observe the state
    /// of the world while a publish is genuinely in flight rather than guessing
    /// at it with a delay.
    /// </summary>
    private sealed class GatedPublisher : ISnapshotPublisher
    {
        private readonly TaskCompletionSource _gate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Documents { get; } = new();

        public void Release() => _gate.TrySetResult();

        public async Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (Documents)
            {
                Documents.Add(document);
            }

            return new PublishResult(PublishOutcome.Success, 200, null);
        }
    }

    private sealed class ThrowingPublisher : ISnapshotPublisher
    {
        public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the publisher exploded");
    }

    private sealed class RecordingLog : IPublishLog
    {
        public List<string> Infos { get; } = new();

        public List<string> Warnings { get; } = new();

        public List<string> Errors { get; } = new();

        public void Info(string message)
        {
            lock (Infos) Infos.Add(message);
        }

        public void Warn(string message)
        {
            lock (Warnings) Warnings.Add(message);
        }

        public void Error(string message)
        {
            lock (Errors) Errors.Add(message);
        }
    }

    private static ProviderUsage Live(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };
}
