using System.Text.Json;
using VibeMeter.Agent;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Runs real agent-host cycles against a recording publisher and a real
/// offline queue to pin the documented end-to-end freshness behaviour: stale
/// underlying data is omitted from the snapshot, and a cycle where every
/// provider is stale publishes NOTHING — not even an empty document, which the
/// API (requiring 1..16 providers) would reject.
/// </summary>
public sealed class AgentHostFreshnessTests : IDisposable
{
    private static readonly Uri ApiBaseUrl = new("https://api.example.com");

    private readonly string _queueDirectory = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"));

    private readonly RecordingPublisher _publisher = new();
    private readonly OfflineQueue _queue;

    public AgentHostFreshnessTests()
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
    public async Task EveryProviderStale_CycleIsSkipped_NothingPublishedOrQueued()
    {
        var host = CreateHost(
            FileDerived("claude", observedAt: DateTimeOffset.UtcNow.AddHours(-3)),
            FileDerived("idle-claude", observedAt: DateTimeOffset.UtcNow.AddDays(-2)));

        await host.RunOneCycleAsync(CancellationToken.None);

        Assert.Empty(_publisher.Documents);
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task MixedFreshness_PublishesOnlyTheFreshProviders()
    {
        var host = CreateHost(
            FileDerived("claude", observedAt: DateTimeOffset.UtcNow.AddHours(-3)),
            Live("codex"));

        await host.RunOneCycleAsync(CancellationToken.None);

        var document = Assert.Single(_publisher.Documents);
        using var json = JsonDocument.Parse(document);
        var providers = json.RootElement.GetProperty("providers");
        Assert.Equal(1, providers.GetArrayLength());
        Assert.Equal("codex", providers[0].GetProperty("providerId").GetString());
        Assert.Equal(0, _queue.Count);
    }

    [Fact]
    public async Task FileDerivedProvider_InsideTheThreshold_IsPublished()
    {
        var host = CreateHost(
            FileDerived("claude", observedAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        await host.RunOneCycleAsync(CancellationToken.None);

        var document = Assert.Single(_publisher.Documents);
        using var json = JsonDocument.Parse(document);
        Assert.Equal(
            "claude",
            json.RootElement.GetProperty("providers")[0].GetProperty("providerId").GetString());
    }

    private AgentHost CreateHost(params ProviderUsage[] results) => new(
        new AgentConfig(ApiBaseUrl, TimeSpan.FromMinutes(5), _queueDirectory, AgentConfig.DefaultStalenessThreshold),
        results.Select(usage => new StubProvider(usage)).Cast<IUsageProvider>().ToArray(),
        new SnapshotMapper(),
        _publisher,
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

    private static ProviderUsage FileDerived(string id, DateTimeOffset observedAt) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        SourceObservedAt = observedAt,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };

    private static ProviderUsage Live(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };
}
