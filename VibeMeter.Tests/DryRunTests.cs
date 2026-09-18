using VibeMeter.Agent;
using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Pins the --dry-run guarantees: the same pipeline the daemon uses produces a
/// full report, but NOTHING is ever published, the offline queue directory is
/// never created, no token is needed or leaked, and the exit code says whether
/// anything was publishable.
/// </summary>
[Collection(EnvironmentCollection.Name)]
public sealed class DryRunTests
{
    private static readonly Uri ApiBaseUrl = new("https://api.example.com");

    private readonly string _queueDirectory = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"),
        "offline-queue");

    [Fact]
    public async Task DryRun_PublishesNothing_AndTouchesNoQueue()
    {
        var publisher = new RecordingPublisher();
        var runner = CreateRunner([Live("codex")], publisher);

        var exitCode = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(publisher.Documents);
        Assert.False(Directory.Exists(_queueDirectory));
    }

    [Fact]
    public async Task DryRun_ReportShowsTheMappedSnapshotAndEachProviderVerdict()
    {
        var output = await RunToString(
            Live("codex", gauges: 2),
            FileDerived("claude", observedAt: DateTimeOffset.UtcNow.AddHours(-3)),
            NotConfigured("google"));

        // The document the mapper produced, in its wire shape.
        Assert.Contains("\"providerId\": \"codex\"", output);
        Assert.Contains("\"schemaVersion\": 1", output);
        // The per-provider verdicts: mapped state, gauge count, omission and why.
        Assert.Contains("state=available", output);
        Assert.Contains("gauges=2", output);
        Assert.Contains("google", output);
        Assert.Contains("state=not-configured", output);
        Assert.Contains("OMITTED by the freshness gate", output);
        Assert.Contains("older than the", output);
        Assert.Contains("DRY-RUN OK", output);
    }

    [Fact]
    public async Task DryRun_WhenNothingIsPublishable_ExitsNonZero()
    {
        var publisher = new RecordingPublisher();
        var output = new StringWriter();
        var runner = CreateRunner(
            [
                FileDerived("claude", observedAt: DateTimeOffset.UtcNow.AddHours(-3)),
                FileDerived("idle", observedAt: DateTimeOffset.UtcNow.AddDays(-2)),
            ],
            publisher,
            output);

        var exitCode = await runner.RunAsync(CancellationToken.None);

        Assert.NotEqual(0, exitCode);
        Assert.Empty(publisher.Documents);
        Assert.Contains("DRY-RUN FAILED", output.ToString());
        Assert.Contains("OMITTED by the freshness gate", output.ToString());
    }

    [Fact]
    public async Task DryRun_NotConfiguredProvidersCountAsPublishable()
    {
        // A brand-new machine reports every provider as not-configured; that is
        // still a publishable, healthy collection result (exit 0).
        var output = await RunToString(
            NotConfigured("claude"),
            NotConfigured("codex"),
            NotConfigured("zai"),
            NotConfigured("google"));

        Assert.Contains("state=not-configured", output);
        Assert.Contains("DRY-RUN OK", output);
    }

    [Fact]
    public async Task DryRun_NeverPrintsTheToken()
    {
        const string sentinelToken = "dry-run-secret-sentinel-9f3ab7";
        var saved = Environment.GetEnvironmentVariable(AgentConfig.TokenVariable);
        try
        {
            Environment.SetEnvironmentVariable(AgentConfig.TokenVariable, sentinelToken);

            var output = await RunToString(Live("codex"));

            Assert.DoesNotContain(sentinelToken, output);
            Assert.DoesNotContain("Bearer", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentConfig.TokenVariable, saved);
        }
    }

    [Fact]
    public async Task DryRun_WorksWithNoTokenConfiguredAtAll()
    {
        // The end-to-end health check a new machine runs: no VIBEMETER_*
        // variables set, the token genuinely absent (never a dummy value) -
        // and the dry-run still collects, maps and succeeds.
        AgentConfig config;
        var saved = AllVariables().ToDictionary(
            variable => variable,
            variable => Environment.GetEnvironmentVariable(variable));
        try
        {
            foreach (var variable in saved.Keys)
            {
                Environment.SetEnvironmentVariable(variable, null);
            }

            config = AgentConfig.FromEnvironment(requireApiBaseUrl: false, requireToken: false);
        }
        finally
        {
            foreach (var (variable, value) in saved)
            {
                Environment.SetEnvironmentVariable(variable, value);
            }
        }

        Assert.Null(config.ApiBaseUrl);

        var publisher = new RecordingPublisher();
        var runner = new DryRunRunner(
            config,
            [new StubProvider(Live("codex"))],
            new SnapshotMapper(),
            publisher,
            new StringWriter());

        var exitCode = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(publisher.Documents);
    }

    private DryRunRunner CreateRunner(ProviderUsage[] usages, ISnapshotPublisher publisher) =>
        CreateRunner(usages, publisher, new StringWriter());

    private DryRunRunner CreateRunner(ProviderUsage[] usages, ISnapshotPublisher publisher, TextWriter output) =>
        new(
            new AgentConfig(ApiBaseUrl, TimeSpan.FromMinutes(5), _queueDirectory, AgentConfig.DefaultStalenessThreshold),
            usages.Select(usage => new StubProvider(usage)).Cast<IUsageProvider>().ToArray(),
            new SnapshotMapper(),
            publisher,
            output);

    private async Task<string> RunToString(params ProviderUsage[] usages)
    {
        var writer = new StringWriter();
        await CreateRunner(usages, new RecordingPublisher(), writer).RunAsync(CancellationToken.None);
        return writer.ToString();
    }

    private static string[] AllVariables() =>
    [
        AgentConfig.ApiBaseUrlVariable,
        AgentConfig.IntervalSecondsVariable,
        AgentConfig.TokenVariable,
        AgentConfig.StalenessMinutesVariable,
    ];

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

    private static ProviderUsage Live(string id, int gauges = 1) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = Enumerable.Range(1, gauges)
            .Select(index => new UsageGauge($"g{index}", $"Gauge {index}", null, 50, null))
            .ToList(),
    };

    private static ProviderUsage FileDerived(string id, DateTimeOffset observedAt) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        SourceObservedAt = observedAt,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };

    private static ProviderUsage NotConfigured(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.NotConfigured,
    };
}
