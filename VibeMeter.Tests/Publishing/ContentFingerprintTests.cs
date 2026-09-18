using VibeMeter.Core;
using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The content fingerprint answers one question - "has anything actually
/// changed?" - and it is only useful if it answers it for the FIGURES and not
/// for the clock. These pin both halves: observedAt must be invisible to it,
/// and every part of the reading a reader would care about must not be.
/// </summary>
public sealed class ContentFingerprintTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateTime FixedResetAt = new(2026, 9, 17, 9, 30, 0, DateTimeKind.Utc);

    private readonly SnapshotMapper _mapper = new();

    [Fact]
    public void IdenticalFigures_ProduceTheSameFingerprint()
    {
        Assert.Equal(Fingerprint(), Fingerprint());
    }

    [Fact]
    public void ObservedAt_IsInvisibleToTheFingerprint()
    {
        // The whole point: the same reading published half an hour apart is
        // still the same reading, so a host with nothing new to say can tell.
        Assert.Equal(
            Fingerprint(observedAt: FixedUtcNow),
            Fingerprint(observedAt: FixedUtcNow.AddMinutes(37)));
    }

    [Fact]
    public void TheIdempotencyKey_StillChangesWithObservedAt()
    {
        // The guard rail on the obvious "fix": if the KEY ever stopped
        // depending on observedAt, two genuinely different documents could
        // collide on one key and the API would answer CONFLICT. The fingerprint
        // exists precisely so the key never has to.
        var earlier = Map(observedAt: FixedUtcNow);
        var later = Map(observedAt: FixedUtcNow.AddMinutes(37));

        Assert.NotEqual(IdempotencyKey.For(earlier.Json), IdempotencyKey.For(later.Json));
        Assert.Equal(ContentFingerprint.For(earlier), ContentFingerprint.For(later));
    }

    [Fact]
    public void AFingerprint_IsNeverAnIdempotencyKey()
    {
        var mapped = Map();

        Assert.StartsWith("f1-", ContentFingerprint.For(mapped));
        Assert.StartsWith("v1-", IdempotencyKey.For(mapped.Json));
        Assert.NotEqual(IdempotencyKey.For(mapped.Json), ContentFingerprint.For(mapped));
    }

    [Fact]
    public void APercentageChange_ChangesTheFingerprint()
    {
        Assert.NotEqual(Fingerprint(), Fingerprint(percent: 49));
    }

    [Fact]
    public void AProviderAppearingOrDisappearing_ChangesTheFingerprint()
    {
        var one = ContentFingerprint.For(_mapper.Map([Usage("codex")], FixedUtcNow));
        var two = ContentFingerprint.For(_mapper.Map([Usage("codex"), Usage("claude")], FixedUtcNow));
        var other = ContentFingerprint.For(_mapper.Map([Usage("claude")], FixedUtcNow));

        Assert.NotEqual(one, two);
        Assert.NotEqual(one, other);
        Assert.NotEqual(two, other);
    }

    [Fact]
    public void AResetTimeChanging_ChangesTheFingerprint()
    {
        // A window rolling over is real news even when the percentage happens
        // to land back on the value it had before.
        Assert.NotEqual(Fingerprint(), Fingerprint(resetAt: FixedResetAt.AddHours(5)));
    }

    [Fact]
    public void AStateOrPlanChange_ChangesTheFingerprint()
    {
        Assert.NotEqual(Fingerprint(), Fingerprint(state: ProviderState.Error));
        Assert.NotEqual(Fingerprint(), Fingerprint(planLabel: "Max 20x"));
    }

    private string Fingerprint(
        string providerId = "codex",
        ProviderState state = ProviderState.Ok,
        int percent = 50,
        DateTime? resetAt = null,
        string? planLabel = "Pro",
        DateTimeOffset? observedAt = null) =>
        ContentFingerprint.For(Map(providerId, state, percent, resetAt, planLabel, observedAt));

    private MappedSnapshot Map(
        string providerId = "codex",
        ProviderState state = ProviderState.Ok,
        int percent = 50,
        DateTime? resetAt = null,
        string? planLabel = "Pro",
        DateTimeOffset? observedAt = null) =>
        _mapper.Map(
            [Usage(providerId, state, percent, resetAt ?? FixedResetAt, planLabel)],
            observedAt ?? FixedUtcNow);

    private static ProviderUsage Usage(
        string providerId,
        ProviderState state = ProviderState.Ok,
        int percent = 50,
        DateTime? resetAt = null,
        string? planLabel = "Pro") => new()
    {
        ProviderId = providerId,
        DisplayName = providerId,
        State = state,
        PlanLabel = planLabel,
        // The reset pair only survives mapping when BOTH halves are present and
        // in range, so the window is set alongside the timestamp.
        Gauges = [new UsageGauge("primary", "Primary", null, percent, resetAt ?? FixedResetAt, null, 18_000)],
    };
}
