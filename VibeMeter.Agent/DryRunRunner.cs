using System.Text.Json;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;

namespace VibeMeter.Agent;

/// <summary>
/// Fills the agent's publisher seat on a --dry-run without any quiet way to
/// publish: if a dry-run path ever tries to send a snapshot, this throws and
/// the run fails loudly. Production wiring puts this in the seat; tests put a
/// recording publisher there instead and assert nothing ever reached it.
/// </summary>
public sealed class NeverPublishPublisher : ISnapshotPublisher
{
    public Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("A dry-run must never publish - reaching the publisher is a bug.");
}

/// <summary>
/// Implements <c>--dry-run</c>: one pass through the same
/// <see cref="SnapshotPipeline"/> the daemon uses, then a human-readable
/// report. Nothing is published, the offline queue is never touched (the
/// pipeline cannot reach either), and no access token is read — a dry-run
/// works on a machine with no authentication configured at all, which is the
/// point: verifying collection before auth exists.
/// </summary>
/// <remarks>
/// SECURITY (hard rule): the report contains the MAPPED snapshot and a
/// per-provider summary (ids, wire states, gauge counts, freshness verdicts)
/// only — never the bearer token, provider credentials, or raw provider
/// responses. There is deliberately no switch that dumps raw provider data.
/// </remarks>
public sealed class DryRunRunner
{
    // Pretty-printed for humans. Values and field order are identical to the
    // wire document; only whitespace differs, and the report says so.
    private static readonly JsonSerializerOptions PrettyOptions = new(SnapshotJson.Options)
    {
        WriteIndented = true,
    };

    private readonly AgentConfig _config;
    private readonly IReadOnlyList<IUsageProvider> _providers;
    private readonly SnapshotMapper _mapper;

    public DryRunRunner(
        AgentConfig config,
        IReadOnlyList<IUsageProvider> providers,
        SnapshotMapper mapper,
        ISnapshotPublisher publisher,
        TextWriter output)
    {
        _config = config;
        _providers = providers;
        _mapper = mapper;
        Publisher = publisher;
        Output = output;
    }

    /// <summary>
    /// The publisher seat, exposed so tests can drive this exact path with a
    /// recording publisher and assert it was never invoked. A dry-run never
    /// calls it: production fills the seat with <see cref="NeverPublishPublisher"/>.
    /// </summary>
    public ISnapshotPublisher Publisher { get; }

    /// <summary>Where the report is written; Console.Out in production.</summary>
    public TextWriter Output { get; }

    /// <summary>
    /// Collects and reports. Returns 0 when at least one provider report would
    /// be published, and non-zero when none would (including when collection
    /// failed outright) - usable as a scripted health check.
    /// </summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await Output.WriteLineAsync(
            $"VibeMeter agent dry-run - collecting from {_providers.Count} provider(s); " +
            "nothing will be published and no token is required.");

        var pipeline = new SnapshotPipeline(_providers, _mapper, _config.StalenessThreshold);
        var collected = await pipeline.CollectGateMapAsync(cancellationToken);
        if (collected is null)
        {
            await Output.WriteLineAsync("DRY-RUN FAILED - collection failed, so no snapshot could be produced at all.");
            return 1;
        }

        WriteReport(collected);
        return collected.Mapped.Request.Providers?.Count > 0 ? 0 : 1;
    }

    private void WriteReport(CycleCollection collected)
    {
        var request = collected.Mapped.Request;
        var providers = request.Providers ?? [];

        Output.WriteLine();
        Output.WriteLine(
            "Snapshot that WOULD be published (pretty-printed for readability - the wire document is " +
            "exactly this JSON without indentation, so only whitespace differs):");
        Output.WriteLine(JsonSerializer.Serialize(request, PrettyOptions));

        Output.WriteLine();
        Output.WriteLine(_config.ApiBaseUrl is { } apiBaseUrl
            ? $"Would POST to {apiBaseUrl} - nothing was sent."
            : "No API base URL configured - nothing could be sent even outside a dry-run.");

        if (collected.Freshness.Notes.Count > 0)
        {
            Output.WriteLine();
            Output.WriteLine("Freshness notes:");
            foreach (var note in collected.Freshness.Notes)
            {
                Output.WriteLine($"  {note}");
            }
        }

        if (collected.Mapped.Warnings.Count > 0)
        {
            Output.WriteLine();
            Output.WriteLine("Mapping warnings:");
            foreach (var warning in collected.Mapped.Warnings)
            {
                Output.WriteLine($"  {warning}");
            }
        }

        Output.WriteLine();
        Output.WriteLine("Per-provider summary:");

        var wouldPublish = providers
            .Where(provider => provider.ProviderId is not null)
            .GroupBy(provider => provider.ProviderId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        // The gate keeps or drops whole reports; matching by instance keeps the
        // summary honest even if two reports ever shared a provider id.
        var keptByGate = new HashSet<ProviderUsage>(collected.Freshness.Publishable);

        foreach (var usage in collected.Collected)
        {
            if (usage is null)
            {
                continue;
            }

            if (!keptByGate.Contains(usage))
            {
                Output.WriteLine(
                    $"  {usage.ProviderId,-16} OMITTED by the freshness gate - {DescribeOmission(usage, collected.Freshness.Omissions)}");
                continue;
            }

            if (wouldPublish.TryGetValue(usage.ProviderId, out var snapshot))
            {
                Output.WriteLine(
                    $"  {usage.ProviderId,-16} state={snapshot.State,-16} gauges={snapshot.Gauges?.Count ?? 0}  would be published");
            }
            else
            {
                Output.WriteLine(
                    $"  {usage.ProviderId,-16} kept by the freshness gate but dropped while mapping (duplicate id or provider limit) - see the mapping warnings above.");
            }
        }

        var collectedCount = collected.Collected.Count(usage => usage is not null);
        var publishedCount = providers.Count;
        var omittedCount = collected.Freshness.Omissions.Count;
        var droppedCount = Math.Max(0, collectedCount - publishedCount - omittedCount);

        Output.WriteLine();
        Output.WriteLine(publishedCount > 0
            ? $"DRY-RUN OK - {publishedCount} of {collectedCount} provider report(s) would be published " +
              $"({omittedCount} omitted as stale, {droppedCount} dropped while mapping). Nothing was published."
            : $"DRY-RUN FAILED - 0 of {collectedCount} provider report(s) would be published " +
              $"({omittedCount} omitted as stale, {droppedCount} dropped while mapping), so the agent would publish nothing at all.");
    }

    /// <summary>The gate's omission lines all start "Provider '&lt;id&gt;' omitted: ";
    /// the summary shows the reason without the prefix.</summary>
    private static string DescribeOmission(ProviderUsage usage, IReadOnlyList<string> omissions)
    {
        var prefix = $"Provider '{usage.ProviderId}' omitted: ";
        var match = omissions.FirstOrDefault(omission => omission.StartsWith(prefix, StringComparison.Ordinal));
        return match is null
            ? "its underlying data is older than the staleness threshold."
            : match[prefix.Length..];
    }
}
