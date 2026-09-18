using System.Text.Json;
using System.Text.RegularExpressions;
using VibeMeter.Publishing;
using VibeMeter.Core;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The Idempotency-Key header is derived from the exact document bytes so the
/// server can de-duplicate retries. Any instability means duplicate rows; any
/// drift between mapped.Json and a re-serialised request means a retry after a
/// restart carries a different key.
/// </summary>
public sealed class IdempotencyKeyTests
{
    // Mirrors the API's IdempotencyKeyPattern.
    private static readonly Regex ApiKeyPattern = new("^[A-Za-z0-9][A-Za-z0-9._:-]*$", RegexOptions.Compiled);

    private static readonly DateTimeOffset FixedUtcNow = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);

    private readonly SnapshotMapper _mapper = new();

    [Fact]
    public void IdenticalInput_ProducesIdenticalKey()
    {
        var first = IdempotencyKey.For(Map().Json);
        var second = IdempotencyKey.For(Map().Json);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Key_IsStableAcrossReSerialisation()
    {
        var mapped = Map();
        var reSerialised = JsonSerializer.Serialize(mapped.Request, SnapshotJson.Options);

        Assert.Equal(mapped.Json, reSerialised);
        Assert.Equal(IdempotencyKey.For(mapped.Json), IdempotencyKey.For(reSerialised));
    }

    [Fact]
    public void AnyMaterialChange_ProducesADifferentKey()
    {
        var baseline = IdempotencyKey.For(Map().Json);

        var changed = new[]
        {
            IdempotencyKey.For(Map(state: ProviderState.Error).Json),
            IdempotencyKey.For(Map(percent: 43).Json),
            IdempotencyKey.For(Map(providerId: "claude").Json),
            IdempotencyKey.For(Map(gaugeId: "secondary").Json),
            IdempotencyKey.For(Map(observedAt: FixedUtcNow.AddSeconds(1)).Json),
        };

        Assert.All(changed, key => Assert.NotEqual(baseline, key));
    }

    [Fact]
    public void Key_SatisfiesTheApiHeaderContract()
    {
        // Mirrors the API's maximum idempotency-key length.
        const int maximumKeyLength = 128;

        var key = IdempotencyKey.For(Map().Json);

        Assert.True(
            key.Length <= maximumKeyLength,
            $"Key is {key.Length} characters; the API accepts at most {maximumKeyLength}.");
        Assert.Matches(ApiKeyPattern, key);
    }

    [Fact]
    public void IdenticalSnapshotQueuedAndRepublished_KeepsTheSameKey()
    {
        // The offline queue keys entries by the document hash; a queued
        // document re-published later must reuse the key of the original.
        var original = Map();
        var queued = original.Json;
        var republished = JsonSerializer.Serialize(_mapper.Map(
            SnapshotUsages(),
            FixedUtcNow).Request, SnapshotJson.Options);

        Assert.Equal(queued, republished);
        Assert.Equal(IdempotencyKey.For(queued), IdempotencyKey.For(republished));
    }

    private static ProviderUsage[] SnapshotUsages() =>
    [
        new ProviderUsage
        {
            ProviderId = "codex",
            DisplayName = "Codex",
            State = ProviderState.Ok,
            Gauges = [new UsageGauge("primary", "Primary", null, 42, null)],
        },
    ];

    private MappedSnapshot Map(
        string providerId = "codex",
        ProviderState state = ProviderState.Ok,
        int percent = 42,
        string gaugeId = "primary",
        DateTimeOffset? observedAt = null) =>
        _mapper.Map(
        [
            new ProviderUsage
            {
                ProviderId = providerId,
                DisplayName = providerId,
                State = state,
                Gauges = [new UsageGauge(gaugeId, "Primary", null, percent, null)],
            },
        ], observedAt ?? FixedUtcNow);
}
