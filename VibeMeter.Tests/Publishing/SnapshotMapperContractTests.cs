using System.Globalization;
using System.Text.Json;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// Locks the wire contract the collection API enforces
/// (Webcoda-App: Features/AiUsage/AiUsageRequestValidator). The API rejects a
/// wrong shape with a silent 400 on every publish cycle, so these tests assert
/// the serialised JSON, not just the mapped objects.
/// </summary>
public sealed class SnapshotMapperContractTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);

    /// <summary>The exact state vocabulary the API accepts (ordinal match).</summary>
    private static readonly string[] AllowedStates =
    [
        "available",
        "not-configured",
        "disabled",
        "stale",
        "error",
    ];

    private readonly SnapshotMapper _mapper = new();

    // ---------------------------------------------------------------- 1

    [Fact]
    public void SchemaVersion_IsAlwaysOne_InObjectAndJson()
    {
        Assert.Equal(1, SnapshotRequest.CurrentSchemaVersion);

        foreach (var usages in new[]
                 {
                     Array.Empty<ProviderUsage>(),
                     new[] { Usage() },
                     Enumerable.Range(1, 20).Select(i => Usage(id: $"p-{i:00}")).ToArray(),
                 })
        {
            var mapped = _mapper.Map(usages, FixedUtcNow);

            Assert.Equal(1, mapped.Request.SchemaVersion);

            using var document = JsonDocument.Parse(mapped.Json);
            Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }
    }

    // ---------------------------------------------------------------- 2

    [Theory]
    [InlineData(10, 0)]
    [InlineData(5, 30)]
    [InlineData(-5, -30)]
    public void ObservedAt_IsSerialisedAsUtcWithZeroOffset_EvenForANonUtcCaller(int hours, int minutes)
    {
        var callerLocal = new DateTimeOffset(2026, 9, 17, 14, 30, 0, new TimeSpan(hours, minutes, 0));

        var mapped = _mapper.Map([Usage()], callerLocal);

        Assert.Equal(TimeSpan.Zero, mapped.Request.ObservedAt.Offset);

        using var document = JsonDocument.Parse(mapped.Json);
        var raw = document.RootElement.GetProperty("observedAt").GetString();
        var parsed = DateTimeOffset.Parse(raw!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, parsed.Offset);
        Assert.Equal(callerLocal.ToUniversalTime(), parsed);
    }

    // ---------------------------------------------------------------- 3

    [Fact]
    public void EveryProviderState_EmitsAStateTheApiAccepts()
    {
        foreach (var state in Enum.GetValues<ProviderState>())
        {
            var mapped = _mapper.Map([Usage(state: state)], FixedUtcNow);

            using var document = JsonDocument.Parse(mapped.Json);
            var wireState = document.RootElement
                .GetProperty("providers")[0]
                .GetProperty("state")
                .GetString();

            Assert.Contains(wireState, AllowedStates);
            Assert.Equal(ExpectedWireState(state), wireState);
        }
    }

    // Exhaustive on purpose, with no wildcard arm: a member added to
    // ProviderState later throws here at runtime until its wire mapping is
    // decided explicitly, instead of silently reusing whatever the mapper's
    // fallback produces.
    private static string ExpectedWireState(ProviderState state) => state switch
    {
        ProviderState.Ok => "available",
        ProviderState.Loading => "stale",
        ProviderState.NotConfigured => "not-configured",
        ProviderState.Error => "error",
        ProviderState.Disabled => "disabled",
        _ => throw new InvalidOperationException(
            $"Unhandled {nameof(ProviderState)} value '{state}': decide which of " +
            "available/not-configured/disabled/stale/error it maps to and add that to this test."),
    };

    // ---------------------------------------------------------------- 4

    [Fact]
    public void MoreThanSixteenProviders_EmitsExactlySixteen_InInputOrder_WithAWarning()
    {
        var usages = Enumerable.Range(1, 20).Select(i => Usage(id: $"p-{i:00}")).ToList();

        var mapped = _mapper.Map(usages, FixedUtcNow);

        var providers = mapped.Request.Providers!;
        Assert.Equal(SnapshotMapper.MaxProviders, providers.Count);
        Assert.Equal(
            Enumerable.Range(1, 16).Select(i => $"p-{i:00}"),
            providers.Select(p => p.ProviderId));

        using var document = JsonDocument.Parse(mapped.Json);
        Assert.Equal(16, document.RootElement.GetProperty("providers").GetArrayLength());

        Assert.Contains(mapped.Warnings, w => w.Contains("Dropping"));
    }

    [Fact]
    public void MoreThanSixteenGauges_EmitsExactlySixteen_WithAWarning()
    {
        var gauges = Enumerable.Range(1, 20)
            .Select(i => new UsageGauge($"g-{i:00}", $"Gauge {i}", null, i * 3, null))
            .ToList();

        var mapped = _mapper.Map([Usage(id: "codex", gauges: gauges)], FixedUtcNow);

        var provider = Assert.Single(mapped.Request.Providers!);
        Assert.Equal(SnapshotMapper.MaxGauges, provider.Gauges!.Count);
        Assert.Equal("g-01", provider.Gauges[0].Id);
        Assert.Equal("g-16", provider.Gauges[15].Id);

        Assert.Contains(mapped.Warnings, w => w.Contains("gauges"));
    }

    // ---------------------------------------------------------------- 5

    /// <summary>
    /// The API rejects an empty providers array ("Provide between 1 and 16
    /// providers"), and the mapper returns exactly that for an empty cycle.
    /// That is only safe because AgentHost.RunOneCycleAsync checks the mapped
    /// provider count and publishes nothing when it is zero — this test pins
    /// the mapper's shape (well-formed, no crash, no fabricated providers); the
    /// host-side guard is what keeps the document off the wire.
    /// </summary>
    [Fact]
    public void ZeroProviders_ProducesAWellFormedButUnpublishableDocument()
    {
        var mapped = _mapper.Map(Array.Empty<ProviderUsage>(), FixedUtcNow);

        Assert.Empty(mapped.Request.Providers!);
        Assert.Empty(mapped.Warnings);

        using var document = JsonDocument.Parse(mapped.Json);
        var providers = document.RootElement.GetProperty("providers");
        Assert.Equal(JsonValueKind.Array, providers.ValueKind);
        Assert.Equal(0, providers.GetArrayLength());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void NullProviderReports_AreSkipped()
    {
        var mapped = _mapper.Map([null!, Usage(id: "codex"), null!], FixedUtcNow);

        var provider = Assert.Single(mapped.Request.Providers!);
        Assert.Equal("codex", provider.ProviderId);
    }

    // ---------------------------------------------------------------- 6

    [Fact]
    public void SerialisedJson_ContainsOnlyContractFields()
    {
        // Every VibeMeter-specific member is populated so any leak onto the
        // wire shows up as an unexpected property below.
        var usage = new ProviderUsage
        {
            ProviderId = "codex",
            DisplayName = "Codex CLI",
            State = ProviderState.Ok,
            ErrorMessage = "unused on the wire",
            PlanLabel = "Pro",
            AvailableCount = 5,
            ResetNote = "unused on the wire",
            Notice = "unused on the wire",
            ResetCredits = [new ResetCredit("active", DateTime.Now, DateTime.Now.AddDays(1), null)],
            Gauges = [new UsageGauge("primary", "Primary", "5-hour window", 42, null, "tooltip text")],
            ExtensionData = new { Anything = true },
            FetchedAt = DateTime.Now,
        };

        var mapped = _mapper.Map([usage], FixedUtcNow);
        using var document = JsonDocument.Parse(mapped.Json);
        var root = document.RootElement;

        Assert.Equal(
            ["observedAt", "providers", "schemaVersion"],
            PropertyNames(root).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        var provider = root.GetProperty("providers")[0];
        Assert.Equal(
            ["gauges", "planLabel", "providerId", "state"],
            PropertyNames(provider).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal("codex", provider.GetProperty("providerId").GetString());
        Assert.Equal("available", provider.GetProperty("state").GetString());

        var gauge = provider.GetProperty("gauges")[0];
        Assert.Equal(
            ["id", "percentRemaining", "resetAt", "resetWindowSeconds", "subtitle", "title"],
            PropertyNames(gauge).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(42, gauge.GetProperty("percentRemaining").GetInt32());
    }

    /// <summary>
    /// The API accepts resetAt only paired with resetWindowSeconds. The mapper
    /// never sends half a pair: a reset time without a window (the provider does
    /// not report a window length) and a window without a reset time are both
    /// mapped to two nulls.
    /// </summary>
    [Fact]
    public void ResetFields_AreEmittedOnlyAsAPair_NeverHalfOfOne()
    {
        var resetWithinRange = new DateTime(2026, 9, 18, 4, 30, 0, DateTimeKind.Utc);
        var gauges = new[]
        {
            new UsageGauge("time-only", "Time only", null, 42, resetWithinRange),
            new UsageGauge("window-only", "Window only", null, 42, null, null, 86_400),
        };

        var mapped = _mapper.Map([Usage(id: "codex", gauges: gauges)], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var wireGauges = document.RootElement.GetProperty("providers")[0].GetProperty("gauges");
        Assert.Equal(2, wireGauges.GetArrayLength());
        foreach (var gauge in wireGauges.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, gauge.GetProperty("resetAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, gauge.GetProperty("resetWindowSeconds").ValueKind);
        }
    }

    private static IEnumerable<string> PropertyNames(JsonElement element) =>
        element.EnumerateObject().Select(property => property.Name);

    private static ProviderUsage Usage(
        string id = "codex",
        ProviderState state = ProviderState.Ok,
        IReadOnlyList<UsageGauge>? gauges = null,
        string? planLabel = null) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = state,
        PlanLabel = planLabel,
        Gauges = gauges ?? Array.Empty<UsageGauge>(),
    };
}
