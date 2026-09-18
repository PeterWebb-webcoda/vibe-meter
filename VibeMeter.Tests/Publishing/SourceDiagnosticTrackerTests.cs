using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The once-per-change rule for source diagnostics: a provider that falls back to a
/// lesser source is logged when that starts, when the reason changes and when it ends —
/// and never on the cycles in between, because the tray refreshes every minute into an
/// append-only log.
/// </summary>
public sealed class SourceDiagnosticTrackerTests
{
    private const string Timeout = "Anthropic usage API not used: the usage endpoint did not respond within 5s";
    private const string Forbidden = "Anthropic usage API not used: the usage endpoint returned HTTP 403";

    private readonly SourceDiagnosticTracker _tracker = new();

    [Fact]
    public void TheFirstFallback_IsNoted_WithTheProviderTheSourceAndTheReason()
    {
        var line = _tracker.NoteChange(FromCache("claude", Forbidden));

        Assert.NotNull(line);
        Assert.Contains("'claude'", line);
        Assert.Contains("Claude Code CLI cache", line);
        Assert.Contains(Forbidden, line);
    }

    [Fact]
    public void TheSameReasonAgain_IsSilent_HoweverManyCyclesItLasts()
    {
        Assert.NotNull(_tracker.NoteChange(FromCache("claude", Forbidden)));

        for (var cycle = 0; cycle < 60; cycle++)
        {
            Assert.Null(_tracker.NoteChange(FromCache("claude", Forbidden)));
        }
    }

    [Fact]
    public void AChangedReason_IsNoted_BecauseItIsADifferentFault()
    {
        Assert.NotNull(_tracker.NoteChange(FromCache("claude", Timeout)));
        Assert.Null(_tracker.NoteChange(FromCache("claude", Timeout)));

        var changed = _tracker.NoteChange(FromCache("claude", Forbidden));
        Assert.NotNull(changed);
        Assert.Contains(Forbidden, changed);
        Assert.DoesNotContain(Timeout, changed);
    }

    [Fact]
    public void Recovery_IsNoted_AndQuotesWhatItRecoveredFrom()
    {
        Assert.NotNull(_tracker.NoteChange(FromCache("claude", Forbidden)));

        var recovered = _tracker.NoteChange(Live("claude"));

        Assert.NotNull(recovered);
        Assert.Contains("'claude'", recovered);
        Assert.Contains("Anthropic usage API", recovered);
        Assert.Contains(Forbidden, recovered);

        // Recovered is the steady state again: nothing more to say.
        Assert.Null(_tracker.NoteChange(Live("claude")));
    }

    [Fact]
    public void AProviderThatNeverFellBack_IsNeverMentioned()
    {
        Assert.Null(_tracker.NoteChange(Live("claude")));
        Assert.Null(_tracker.NoteChange(Live("codex")));
        Assert.Null(_tracker.NoteChange(new ProviderUsage { ProviderId = "zai", State = ProviderState.Ok }));
    }

    [Fact]
    public void AFailedOrUnconfiguredReport_IsNotASourceNote_AndResetsTheProvider()
    {
        // Those states are the provider's own failure path, logged or displayed as such.
        Assert.NotNull(_tracker.NoteChange(FromCache("claude", Forbidden)));

        Assert.Null(_tracker.NoteChange(new ProviderUsage { ProviderId = "claude", State = ProviderState.Error }));
        Assert.Null(_tracker.NoteChange(new ProviderUsage { ProviderId = "claude", State = ProviderState.NotConfigured }));

        // Back to a working fallback afterwards: that is a fresh start, so it is noted again.
        Assert.NotNull(_tracker.NoteChange(FromCache("claude", Forbidden)));
    }

    [Fact]
    public void ProvidersAreTrackedIndependently()
    {
        Assert.NotNull(_tracker.NoteChange(FromCache("claude", Forbidden)));
        Assert.NotNull(_tracker.NoteChange(FromCache("other", Timeout)));
        Assert.Null(_tracker.NoteChange(FromCache("claude", Forbidden)));
        Assert.Null(_tracker.NoteChange(FromCache("other", Timeout)));
    }

    private static ProviderUsage FromCache(string id, string diagnostic) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        SourceLabel = "Claude Code CLI cache",
        SourceDiagnostic = diagnostic,
        SourceObservedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };

    private static ProviderUsage Live(string id) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        SourceLabel = "Anthropic usage API",
        SourceObservedAt = DateTimeOffset.UtcNow,
        Gauges = [new UsageGauge("g", "Gauge", null, 50, null)],
    };
}
