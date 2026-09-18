using System.Text.Json;
using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The disabled-provider rule, which exists because of how the collection API
/// stores readings: the latest is kept PER PROVIDER, so a provider a document
/// does not mention keeps its previous stored reading indefinitely. The tray
/// app only fetches the providers the user has enabled, so switching one off
/// must be SAID rather than implied by silence - otherwise a switched-off
/// provider's final percentage would sit on the phone looking current for ever.
/// </summary>
public sealed class ProviderRosterTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"));

    private readonly OfflineQueue _queue;

    public ProviderRosterTests()
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
    public void AProviderTheHostDidNotFetch_IsStatedAsDisabled()
    {
        var roster = ProviderRoster.IncludingDisabled(
            [Live("claude")],
            [Known("claude"), Known("codex"), Known("zai")]);

        Assert.Equal(["claude", "codex", "zai"], roster.Select(report => report.ProviderId));
        Assert.Equal(ProviderState.Ok, roster[0].State);
        Assert.Equal(ProviderState.Disabled, roster[1].State);
        Assert.Equal(ProviderState.Disabled, roster[2].State);
    }

    [Fact]
    public void ADisabledReport_CarriesNoGauges()
    {
        // There is no current reading to state, and an empty gauge list is what
        // tells the reader so. A leftover percentage would be the lie this whole
        // rule exists to prevent.
        var roster = ProviderRoster.IncludingDisabled([], [Known("codex")]);

        Assert.Empty(Assert.Single(roster).Gauges);
    }

    [Fact]
    public void TheOrderIsTheRegistrys_WhateverOrderTheFetchesLandedIn()
    {
        // Not cosmetic: ContentFingerprint hashes the provider list INCLUDING
        // its order, so a roster whose order followed fetch timing would read as
        // new figures on every cycle and defeat the publish policy entirely.
        var known = new[] { Known("claude"), Known("codex"), Known("zai"), Known("google") };

        var first = ProviderRoster.IncludingDisabled([Live("zai"), Live("claude")], known);
        var second = ProviderRoster.IncludingDisabled([Live("claude"), Live("zai")], known);

        Assert.Equal(["claude", "codex", "zai", "google"], first.Select(report => report.ProviderId));
        Assert.Equal(first.Select(report => report.ProviderId), second.Select(report => report.ProviderId));
    }

    [Fact]
    public void AReportForSomethingOutsideTheRegistry_IsKeptRatherThanDropped()
    {
        // Should be impossible - the host's cards come from the registry - but
        // losing a real reading over a bookkeeping mismatch would be far worse
        // than appending it.
        var roster = ProviderRoster.IncludingDisabled([Live("mystery")], [Known("codex")]);

        Assert.Equal(["codex", "mystery"], roster.Select(report => report.ProviderId));
        Assert.Equal(ProviderState.Disabled, roster[0].State);
        Assert.Equal(ProviderState.Ok, roster[1].State);
    }

    [Fact]
    public void TheCollectedReportWins_WhateverTheCasingOfTheRegistrysId()
    {
        // ProviderRegistry.Get matches ids case-insensitively; if this did not,
        // a casing disagreement would publish the reading AND a claim that the
        // same provider is switched off.
        var roster = ProviderRoster.IncludingDisabled([Live("Codex")], [Known("codex")]);

        Assert.Equal(ProviderState.Ok, Assert.Single(roster).State);
    }

    [Fact]
    public async Task ADisabledProviderReachesTheWireAsStateDisabled()
    {
        // End to end, because the claim only matters if it survives the
        // freshness gate and the mapper: a disabled report is not a successful
        // fetch, so the gate must pass it through untouched.
        var publisher = new RecordingPublisher();
        var cycle = new SnapshotPublishCycle(
            new SnapshotComposer(new SnapshotMapper(), TimeSpan.FromMinutes(20), NullPublishLog.Instance),
            publisher,
            _queue,
            NullPublishLog.Instance);

        var roster = ProviderRoster.IncludingDisabled([Live("claude")], [Known("claude"), Known("codex")]);

        Assert.Equal(CycleOutcome.Published, await cycle.PublishAsync(roster, CancellationToken.None));

        var document = JsonDocument.Parse(Assert.Single(publisher.Documents));
        var providers = document.RootElement.GetProperty("providers");

        Assert.Equal(2, providers.GetArrayLength());
        Assert.Equal("claude", providers[0].GetProperty("providerId").GetString());
        Assert.Equal("available", providers[0].GetProperty("state").GetString());
        Assert.Equal("codex", providers[1].GetProperty("providerId").GetString());
        Assert.Equal("disabled", providers[1].GetProperty("state").GetString());
        Assert.Equal(0, providers[1].GetProperty("gauges").GetArrayLength());
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

    private sealed class KnownProvider(string id) : IUsageProvider
    {
        public string Id => id;

        public string DisplayName => id;

        public Task<ProviderUsage> FetchAsync() =>
            throw new InvalidOperationException("The roster never fetches; it only names what could have been fetched.");
    }

    private static IUsageProvider Known(string id) => new KnownProvider(id);

    private static ProviderUsage Live(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };
}
