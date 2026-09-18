using VibeMeter.Agent;
using VibeMeter.Publishing;
using VibeMeter.Core;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// Freshness gating: a provider whose UNDERLYING data is older than the
/// threshold must be OMITTED from the snapshot — never downgraded to
/// state="stale", because the collection API merges per provider newest-first
/// by publish time and would let a stale-but-published reading mask a fresh
/// one from a machine actually in use.
/// </summary>
public sealed class FreshnessGateTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan DefaultThreshold = TimeSpan.FromMinutes(20);

    private readonly FreshnessGate _gate = new(DefaultThreshold);

    // --------------------------------------------- the required behaviours

    [Fact]
    public void StaleFileDerivedObservation_IsOmitted_WhileAFreshOneIsKept()
    {
        // Both report Ok: "claude" read from a local file last refreshed 3h ago
        // (the idle-machine trap); "fresh-claude" from one refreshed 5 min ago.
        var mapped = _gate.Apply(
        [
            FileDerived("claude", observedAt: Now.AddHours(-3)),
            FileDerived("fresh-claude", observedAt: Now.AddMinutes(-5)),
        ], Now);

        var publishedIds = mapped.Publishable.Select(u => u.ProviderId).ToArray();
        Assert.Equal(["fresh-claude"], publishedIds);

        var omission = Assert.Single(mapped.Omissions);
        Assert.Contains("'claude'", omission);
        Assert.Contains("3h", omission);
        Assert.Contains("newest-first", omission);
    }

    [Fact]
    public void HttpDerivedProvider_IsNeverOmitted_ForStaleness()
    {
        // Codex and Z.ai are fetched live over HTTP each cycle, so they carry
        // no source observation time at all. Even under an absurdly strict
        // one-minute threshold they must publish.
        var strictGate = new FreshnessGate(TimeSpan.FromMinutes(1));
        var mapped = strictGate.Apply(
        [
            Live("codex"),
            Live("zai"),
        ], Now);

        Assert.Equal(["codex", "zai"], mapped.Publishable.Select(u => u.ProviderId).ToArray());
        Assert.Empty(mapped.Omissions);
    }

    [Fact]
    public void UnknownFreshness_IsKeptNotDropped_AndNotedOncePerProvider()
    {
        var mapped = _gate.Apply([Live("codex")], Now);

        // Kept: an inability to measure age must never silently erase data.
        var published = Assert.Single(mapped.Publishable);
        Assert.Equal("codex", published.ProviderId);
        Assert.Empty(mapped.Omissions);
        Assert.Single(mapped.Notes);

        // The note is per process, not per cycle — the second pass is silent.
        var secondCycle = _gate.Apply([Live("codex")], Now);
        Assert.Single(secondCycle.Publishable);
        Assert.Empty(secondCycle.Notes);
    }

    [Fact]
    public void AllProvidersStale_NothingIsPublishable_AndEveryOmissionIsExplained()
    {
        // An empty publishable list is what makes the host skip the cycle: the
        // API requires 1..16 providers, and publishing an empty document would
        // be a guaranteed 400. AgentHostFreshnessTests pins the host-side skip.
        var mapped = _gate.Apply(
        [
            FileDerived("claude", observedAt: Now.AddHours(-3)),
            FileDerived("claude-desktop", observedAt: Now.AddDays(-2)),
        ], Now);

        Assert.Empty(mapped.Publishable);
        Assert.Equal(2, mapped.Omissions.Count);
        Assert.Contains(mapped.Omissions, o => o.Contains("'claude'"));
        Assert.Contains(mapped.Omissions, o => o.Contains("'claude-desktop'"));
    }

    [Fact]
    public void Threshold_IsRespectedAtTheBoundary()
    {
        // "Older than the threshold" is strict: exactly at the threshold is
        // still fresh, one second past it is stale.
        var mapped = _gate.Apply(
        [
            FileDerived("exactly-at-threshold", observedAt: Now.Subtract(DefaultThreshold)),
            FileDerived("one-second-past", observedAt: Now.Subtract(DefaultThreshold).AddSeconds(-1)),
        ], Now);

        Assert.Equal(
            ["exactly-at-threshold"],
            mapped.Publishable.Select(u => u.ProviderId).ToArray());
        var omission = Assert.Single(mapped.Omissions);
        Assert.Contains("'one-second-past'", omission);
    }

    [Fact]
    public void NotConfiguredDisabledAndError_KeepTheirExistingBehaviour_EvenWhenAncient()
    {
        // Gating is only about data that is present but stale. States with
        // their own wire meaning pass through untouched, whatever timestamps
        // they carry.
        var mapped = _gate.Apply(
        [
            WithState("not-configured", ProviderState.NotConfigured, Now.AddYears(-1)),
            WithState("disabled", ProviderState.Disabled, Now.AddYears(-1)),
            WithState("errored", ProviderState.Error, Now.AddYears(-1)),
        ], Now);

        Assert.Equal(
            ["not-configured", "disabled", "errored"],
            mapped.Publishable.Select(u => u.ProviderId).ToArray());
        Assert.Empty(mapped.Omissions);
        Assert.Empty(mapped.Notes);
    }

    // --------------------------------------------- configuration surface

    [Fact]
    public void DefaultStalenessThreshold_IsTwentyMinutes()
    {
        // The documented default: ride out a few publish intervals of ordinary
        // idleness, stay far inside Claude's shortest (5-hour) window.
        Assert.Equal(TimeSpan.FromMinutes(20), AgentConfig.DefaultStalenessThreshold);
        Assert.Equal(TimeSpan.FromMinutes(20), new AgentConfig(
            new Uri("https://api.example.com"),
            TimeSpan.FromMinutes(5),
            Path.Combine(Path.GetTempPath(), "unused"),
            AgentConfig.DefaultStalenessThreshold).StalenessThreshold);
    }

    [Fact]
    public void NonPositiveThreshold_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FreshnessGate(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FreshnessGate(TimeSpan.FromSeconds(-1)));
    }

    // --------------------------------------------- helpers

    /// <summary>A successful fetch whose data came from local files, carrying the
    /// source's own observation time — the Claude shape.</summary>
    private static ProviderUsage FileDerived(string id, DateTimeOffset observedAt) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        SourceObservedAt = observedAt,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };

    /// <summary>A successful live HTTP fetch — no source observation time by design.</summary>
    private static ProviderUsage Live(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };

    private static ProviderUsage WithState(string id, ProviderState state, DateTimeOffset observedAt) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = state,
        SourceObservedAt = observedAt,
    };
}
