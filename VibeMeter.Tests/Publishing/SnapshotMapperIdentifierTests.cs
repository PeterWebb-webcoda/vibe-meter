using System.Text.Json;
using VibeMeter.Publishing;
using VibeMeter.Core;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// The API only accepts identifiers matching ^[a-z0-9][a-z0-9._-]*$ (max 64
/// characters) for provider and gauge ids, so every id the mapper emits must
/// satisfy that pattern regardless of what a provider reports.
/// </summary>
public sealed class SnapshotMapperIdentifierTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);

    private readonly SnapshotMapper _mapper = new();

    [Theory]
    [InlineData("codex", "codex")]
    [InlineData("Z.ai GLM-4.5", "z.ai-glm-4.5")]
    [InlineData("  claude  ", "claude")]
    [InlineData("", "provider-1")]
    public void ProviderId_IsNormalisedIntoTheApiIdentifierPattern(string reported, string expected)
    {
        var mapped = _mapper.Map([Usage(id: reported)], FixedUtcNow);

        Assert.Equal(expected, mapped.Request.Providers![0].ProviderId);
    }

    [Fact]
    public void AlreadyValidProviderId_ProducesNoWarning()
    {
        var mapped = _mapper.Map([Usage(id: "codex")], FixedUtcNow);

        Assert.Empty(mapped.Warnings);
    }

    [Fact]
    public void NormalisedProviderId_ProducesAWarning()
    {
        var mapped = _mapper.Map([Usage(id: "Z.ai GLM-4.5")], FixedUtcNow);

        Assert.Contains(mapped.Warnings, w => w.Contains("normalised"));
    }

    [Fact]
    public void DuplicateProviderId_KeepsTheFirstReportAndWarns()
    {
        var mapped = _mapper.Map([Usage(id: "codex", state: ProviderState.Ok), Usage(id: "codex", state: ProviderState.Error)], FixedUtcNow);

        var provider = Assert.Single(mapped.Request.Providers!);
        Assert.Equal("available", provider.State);
        Assert.Contains(mapped.Warnings, w => w.Contains("Duplicate provider id"));
    }

    [Fact]
    public void NormalisedGaugeIds_SatisfyTheApiIdentifierPattern()
    {
        var gauges = new[]
        {
            new UsageGauge("5h window!", "5-hour window", null, 40, null),
            new UsageGauge("", "Fallback", null, 50, null),
        };

        var mapped = _mapper.Map([Usage(id: "codex", gauges: gauges)], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var emitted = document.RootElement.GetProperty("providers")[0]
            .GetProperty("gauges").EnumerateArray()
            .Select(g => g.GetProperty("id").GetString())
            .ToArray();

        Assert.Equal(2, emitted.Length);
        Assert.All(emitted, id => Assert.Matches("^[a-z0-9][a-z0-9._-]*$", id!));
    }

    // The API rejects any id longer than 64 characters
    // (the API's identifier rule), so the duplicate-id suffix
    // must never push an id past that limit.
    [Fact]
    public void DuplicateGaugeIdSuffixing_NeverExceedsTheApiIdentifierLimit()
    {
        var maximumLengthId = "g" + new string('a', 63);
        var gauges = new[]
        {
            new UsageGauge(maximumLengthId, "First", null, 10, null),
            new UsageGauge(maximumLengthId, "Second", null, 20, null),
        };

        var mapped = _mapper.Map([Usage(id: "codex", gauges: gauges)], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var emitted = document.RootElement.GetProperty("providers")[0]
            .GetProperty("gauges").EnumerateArray()
            .Select(g => g.GetProperty("id").GetString())
            .ToArray();

        Assert.All(emitted, id => Assert.True(
            id!.Length <= 64,
            $"'{id}' is {id.Length} characters; the API rejects ids longer than 64."));
    }

    private static ProviderUsage Usage(
        string id,
        ProviderState state = ProviderState.Ok,
        IReadOnlyList<UsageGauge>? gauges = null) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = state,
        Gauges = gauges ?? Array.Empty<UsageGauge>(),
    };
}
