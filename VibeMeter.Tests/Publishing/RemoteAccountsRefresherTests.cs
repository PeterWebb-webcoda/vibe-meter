using System.Text.Json;
using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// How often other machines' accounts are read back, what a failure costs, and — the rule
/// that matters most — that nothing read back can ever travel into what this machine
/// publishes. A manual clock stands in for time and a scripted reader for the API, so
/// nothing here waits or touches the network.
/// </summary>
public sealed class RemoteAccountsRefresherTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Start = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
    private static readonly AccountLabels LocalWork = AccountLabels.Create("work", null);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibemeter-tests", Guid.NewGuid().ToString("N"));

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

    // ------------------------------------------------------------ throttling

    [Fact]
    public async Task TheFirstCall_Reads_AndTheNextReadWaitsFiveMinutes()
    {
        var time = new ManualTime(Start);
        var reader = new ScriptedReader(Ok("home"), Ok("home"));
        var refresher = new RemoteAccountsRefresher(reader, LocalWork, NullPublishLog.Instance, time);

        Assert.True(await refresher.RefreshIfDueAsync(CancellationToken.None));
        Assert.Equal("home", Assert.Single(refresher.Current).AccountLabel);

        time.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        Assert.False(await refresher.RefreshIfDueAsync(CancellationToken.None));
        Assert.Equal(1, reader.Calls);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await refresher.RefreshIfDueAsync(CancellationToken.None));
        Assert.Equal(2, reader.Calls);
    }

    [Fact]
    public async Task AFailure_KeepsTheLastGoodSelection()
    {
        var time = new ManualTime(Start);
        var reader = new ScriptedReader(Ok("home"), Failed(503));
        var refresher = new RemoteAccountsRefresher(reader, LocalWork, NullPublishLog.Instance, time);

        await refresher.RefreshIfDueAsync(CancellationToken.None);
        time.Advance(RemoteAccountsRefresher.RefreshInterval);

        Assert.False(await refresher.RefreshIfDueAsync(CancellationToken.None));
        Assert.Equal("home", Assert.Single(refresher.Current).AccountLabel);
    }

    [Fact]
    public async Task A204_ClearsTheSelection()
    {
        // Empty is a real answer, not a failure: the other machine's rows are gone.
        var time = new ManualTime(Start);
        var reader = new ScriptedReader(Ok("home"), new RemoteAccountsReadResult(RemoteAccountsReadStatus.Empty, 204, null));
        var refresher = new RemoteAccountsRefresher(reader, LocalWork, NullPublishLog.Instance, time);

        await refresher.RefreshIfDueAsync(CancellationToken.None);
        time.Advance(RemoteAccountsRefresher.RefreshInterval);

        Assert.True(await refresher.RefreshIfDueAsync(CancellationToken.None));
        Assert.Empty(refresher.Current);
    }

    [Fact]
    public async Task RepeatedFailures_BackOff_10_20_40_ThenCapAtAnHour()
    {
        var time = new ManualTime(Start);
        var reader = new ScriptedReader(Failed(500), Failed(500), Failed(500), Failed(500), Failed(500));
        var refresher = new RemoteAccountsRefresher(reader, LocalWork, NullPublishLog.Instance, time);

        foreach (var expectedMinutes in new[] { 10, 20, 40, 60, 60 })
        {
            await refresher.RefreshIfDueAsync(CancellationToken.None);
            Assert.Equal(time.Now + TimeSpan.FromMinutes(expectedMinutes), refresher.NextDue);

            // Not a minute sooner.
            time.Advance(TimeSpan.FromMinutes(expectedMinutes) - TimeSpan.FromSeconds(1));
            var callsBefore = reader.Calls;
            await refresher.RefreshIfDueAsync(CancellationToken.None);
            Assert.Equal(callsBefore, reader.Calls);
            time.Advance(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task ASuccess_ResetsTheBackoff()
    {
        var time = new ManualTime(Start);
        var reader = new ScriptedReader(Failed(500), Failed(500), Ok("home"), Failed(500));
        var refresher = new RemoteAccountsRefresher(reader, LocalWork, NullPublishLog.Instance, time);

        await refresher.RefreshIfDueAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(10));
        await refresher.RefreshIfDueAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(20));
        await refresher.RefreshIfDueAsync(CancellationToken.None);
        Assert.Equal(time.Now + RemoteAccountsRefresher.RefreshInterval, refresher.NextDue);

        time.Advance(RemoteAccountsRefresher.RefreshInterval);
        await refresher.RefreshIfDueAsync(CancellationToken.None);
        Assert.Equal(time.Now + TimeSpan.FromMinutes(10), refresher.NextDue);
    }

    [Fact]
    public async Task A404_WaitsTheFullHour()
    {
        // An API that predates the endpoint will not grow one in the next ten minutes.
        var time = new ManualTime(Start);
        var reader = new ScriptedReader(new RemoteAccountsReadResult(RemoteAccountsReadStatus.NotSupported, 404, null));
        var refresher = new RemoteAccountsRefresher(reader, LocalWork, NullPublishLog.Instance, time);

        await refresher.RefreshIfDueAsync(CancellationToken.None);

        Assert.Equal(Start + RemoteAccountsRefresher.MaxBackoff, refresher.NextDue);
    }

    [Fact]
    public async Task ARunOfFailures_IsLoggedOnce_AndRecoveryOnce()
    {
        var time = new ManualTime(Start);
        var log = new RecordingLog();
        var reader = new ScriptedReader(Failed(500), Failed(401, RemoteAccountsReadStatus.AuthFailure), Ok("home"), Ok("home"));
        var refresher = new RemoteAccountsRefresher(reader, LocalWork, log, time);

        for (var i = 0; i < 4; i++)
        {
            await refresher.RefreshIfDueAsync(CancellationToken.None);
            time.Advance(RemoteAccountsRefresher.MaxBackoff);
        }

        Assert.Equal(2, log.Infos.Count);
        Assert.Contains("HTTP 500", log.Infos[0], StringComparison.Ordinal);
        Assert.Contains("again", log.Infos[1], StringComparison.Ordinal);
        Assert.Empty(log.Warnings);
        Assert.Empty(log.Errors);
    }

    // ------------------------------------------------------------ the host

    [Fact]
    public async Task RemoteAccounts_NeverEnterThePublishedSnapshot()
    {
        // The feedback loop this must never form: machine A reads B's account back,
        // publishes it as its own, B reads it back from A, and so on. Run two cycles with
        // the read-back returning another machine's Claude (and, for good measure, a
        // Codex account outside the allow-list) and check every byte that went out.
        var publisher = new RecordingPublisher();
        var reader = new ScriptedReader(Ok("home", plan: "Remote Plan", percent: 7m), Ok("home", plan: "Remote Plan", percent: 7m));
        reader.BeforeRead = () => Assert.NotEmpty(publisher.Documents);

        IReadOnlyList<RemoteAccount>? raised = null;
        using var host = CreateHost(publisher, reader, NullPublishLog.Instance);
        host.RemoteAccountsChanged += accounts => raised = accounts;

        for (var cycle = 0; cycle < 2; cycle++)
        {
            Assert.True(host.Publish([Local("claude"), Local("codex")]));
            await host.Current.WaitAsync(Patience);
        }

        // The read did happen, after the publish, and the host holds the result...
        Assert.Equal(1, reader.Calls);
        Assert.Equal("home", Assert.Single(raised!).AccountLabel);
        Assert.Equal("home", Assert.Single(host.RemoteAccounts).AccountLabel);

        // ...and none of it was published.
        Assert.Equal(2, publisher.Documents.Count);
        foreach (var document in publisher.Documents)
        {
            Assert.DoesNotContain("home", document, StringComparison.Ordinal);
            Assert.DoesNotContain("Remote Plan", document, StringComparison.Ordinal);

            using var json = JsonDocument.Parse(document);
            var providers = json.RootElement.GetProperty("providers").EnumerateArray().ToList();
            Assert.Equal(["claude", "codex"], providers.Select(p => p.GetProperty("providerId").GetString()));
            foreach (var provider in providers)
            {
                Assert.Equal("work", provider.GetProperty("accountLabel").GetString());
                var gauge = Assert.Single(provider.GetProperty("gauges").EnumerateArray());
                Assert.Equal(50m, gauge.GetProperty("percentRemaining").GetDecimal());
            }
        }
    }

    [Fact]
    public async Task ASubscriberThatThrows_CostsNothingButTheEvent()
    {
        var publisher = new RecordingPublisher();
        var log = new RecordingLog();
        using var host = CreateHost(publisher, new ScriptedReader(Ok("home")), log);
        host.RemoteAccountsChanged += _ => throw new InvalidOperationException("the window exploded");

        Assert.True(host.Publish([Local("claude")]));
        await host.Current.WaitAsync(Patience);

        Assert.Equal(TaskStatus.RanToCompletion, host.Current.Status);
        Assert.Single(publisher.Documents);
        Assert.Contains(log.Errors, message => message.Contains("the window exploded", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHostWithoutAReader_ReadsNothingBack()
    {
        var publisher = new RecordingPublisher();
        var log = NullPublishLog.Instance;
        using var host = new DesktopPublishHost(
            new SnapshotPublishCycle(
                new SnapshotComposer(new SnapshotMapper(LocalWork), TimeSpan.FromMinutes(20), log),
                publisher,
                new OfflineQueue(Path.Combine(_root, "offline-queue")),
                log),
            log);
        var raised = false;
        host.RemoteAccountsChanged += _ => raised = true;

        Assert.True(host.Publish([Local("claude")]));
        await host.Current.WaitAsync(Patience);

        Assert.False(raised);
        Assert.Empty(host.RemoteAccounts);
    }

    private DesktopPublishHost CreateHost(ISnapshotPublisher publisher, IRemoteAccountsReader reader, IPublishLog log) =>
        new(
            new SnapshotPublishCycle(
                new SnapshotComposer(new SnapshotMapper(LocalWork), TimeSpan.FromMinutes(20), log),
                publisher,
                new OfflineQueue(Path.Combine(_root, "offline-queue")),
                log),
            log,
            publisherLifetime: null,
            remoteAccounts: new RemoteAccountsRefresher(reader, LocalWork, log));

    private static ProviderUsage Local(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };

    /// <summary>A read-back holding this machine's own Claude ("work"), another machine's
    /// ("home"), and a Codex account outside the allow-list.</summary>
    private static RemoteAccountsReadResult Ok(string otherLabel, string? plan = null, decimal percent = 50m)
    {
        RemoteAccountReading Account(string label) => new(
            "synthetic-" + label, label, "available", plan,
            [new RemoteGauge("claude-5h", "5h", null, percent, null, null)],
            Start, Start);

        return new RemoteAccountsReadResult(
            RemoteAccountsReadStatus.Success,
            200,
            new RemoteAccountsResponse(2, Start, Start,
            [
                new RemoteProviderAccounts("claude", [Account("work"), Account(otherLabel)]),
                new RemoteProviderAccounts("codex", [Account(otherLabel)]),
            ]));
    }

    private static RemoteAccountsReadResult Failed(int status, RemoteAccountsReadStatus kind = RemoteAccountsReadStatus.Failed) =>
        new(kind, status, null);

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan by) => Now += by;
    }

    private sealed class ScriptedReader(params RemoteAccountsReadResult[] script) : IRemoteAccountsReader
    {
        private readonly Queue<RemoteAccountsReadResult> _script = new(script);

        public int Calls { get; private set; }

        public Action? BeforeRead { get; set; }

        public Task<RemoteAccountsReadResult> ReadAsync(CancellationToken cancellationToken)
        {
            BeforeRead?.Invoke();
            Calls++;
            return Task.FromResult(_script.Count > 0
                ? _script.Dequeue()
                : throw new InvalidOperationException("The script ran out: more reads than the test expected."));
        }
    }

    private sealed class RecordingPublisher : ISnapshotPublisher
    {
        public List<string> Documents { get; } = new();

        public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken)
        {
            lock (Documents)
            {
                Documents.Add(document);
            }

            return Task.FromResult(new PublishResult(PublishOutcome.Success, 200, null));
        }
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
}
