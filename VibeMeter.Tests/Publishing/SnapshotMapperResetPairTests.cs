using System.Text.Json;
using VibeMeter.Publishing;
using VibeMeter.Core;
using VibeMeter.Providers.Claude;
using VibeMeter.Providers.Codex;
using VibeMeter.Providers.Google;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// Locks the reset-pair emission rules the collection API enforces
/// (Webcoda-App: Features/AiUsage/AiUsageRequestValidator): resetAt and
/// resetWindowSeconds travel only as a pair; the window must be 60 s..366 d;
/// resetAt must be UTC with a zero offset within observedAt − 1 day ..
/// observedAt + 366 days. Any failure omits BOTH fields — never a clamped or
/// half pairing. Also pins the per-provider window sources (Codex's
/// limit_window_seconds, Claude's named five-hour/seven-day windows, Google's
/// declared window buckets) and that Z.ai — which reports no window — stays
/// null by default.
/// </summary>
public sealed class SnapshotMapperResetPairTests
{
    private static readonly DateTimeOffset FixedUtcNow = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);

    private readonly SnapshotMapper _mapper = new();

    // ---------------------------------------------------------------- pair emission

    [Fact]
    public void GaugeWithAKnownWindow_EmitsBothFields_AndSerialisesResetAtWithZeroUtcOffset()
    {
        var mapped = _mapper.Map([Usage(gauges:
        [
            new UsageGauge("primary", "Primary", null, 42,
                new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc), null, 86_400),
        ])], FixedUtcNow);

        var gauge = mapped.Request.Providers![0].Gauges![0];
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero), gauge.ResetAt);
        Assert.Equal(TimeSpan.Zero, gauge.ResetAt!.Value.Offset);
        Assert.Equal(86_400, gauge.ResetWindowSeconds);

        using var document = JsonDocument.Parse(mapped.Json);
        var wireGauge = document.RootElement.GetProperty("providers")[0].GetProperty("gauges")[0];
        var raw = DateTimeOffset.Parse(
            wireGauge.GetProperty("resetAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind);
        Assert.Equal(TimeSpan.Zero, raw.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 17, 8, 0, 0, TimeSpan.Zero), raw);
        Assert.Equal(86_400, wireGauge.GetProperty("resetWindowSeconds").GetInt32());
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void AnyDateTimeKind_IsSerialisedAsUtcWithZeroOffset(DateTimeKind kind)
    {
        // The providers hand over local (or unspecified) DateTimes; the wire must
        // carry UTC regardless. 20:00 "wall clock" lands inside the accepted
        // range for every machine time zone (±14 h).
        var reset = new DateTime(2026, 9, 17, 20, 0, 0, kind);
        var expectedUtc = kind == DateTimeKind.Utc
            ? new DateTimeOffset(reset, TimeSpan.Zero)
            : new DateTimeOffset(reset, TimeZoneInfo.Local.GetUtcOffset(reset)).ToUniversalTime();

        var mapped = _mapper.Map([Usage(gauges:
        [
            new UsageGauge("primary", "Primary", null, 42, reset, null, 3_600),
        ])], FixedUtcNow);

        Assert.Equal(expectedUtc, mapped.Request.Providers![0].Gauges![0].ResetAt);
        Assert.Equal(TimeSpan.Zero, mapped.Request.Providers[0].Gauges![0].ResetAt!.Value.Offset);
    }

    [Fact]
    public void AnUnrepresentableResetTimestamp_OmitsThePair_RatherThanThrowing()
    {
        // DateTime.MaxValue overflowed through the local-time conversion would
        // throw; the mapper must treat it as out of range instead.
        var mapped = _mapper.Map([Usage(gauges:
        [
            new UsageGauge("primary", "Primary", null, 42, DateTime.MaxValue, null, 3_600),
        ])], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var (resetAt, window) = ResetPairOf(
            document.RootElement.GetProperty("providers")[0].GetProperty("gauges"), 0);
        Assert.Null(resetAt);
        Assert.Null(window);
    }

    [Theory]
    [InlineData(30, 0)]            // window below the 60 s minimum
    [InlineData(31_622_401, 0)]    // window above the 366-day maximum
    [InlineData(3_600, -2)]        // resetAt more than a day before observedAt
    [InlineData(3_600, 400)]       // resetAt more than 366 days after observedAt
    public void ValuesOutsideTheAcceptedBounds_EmitNeitherField(int windowSeconds, int offsetDays)
    {
        var mapped = _mapper.Map([Usage(gauges:
        [
            new UsageGauge("primary", "Primary", null, 42,
                FixedUtcNow.AddDays(offsetDays).UtcDateTime, null, windowSeconds),
        ])], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var wireGauges = document.RootElement.GetProperty("providers")[0].GetProperty("gauges");
        var (resetAt, window) = ResetPairOf(wireGauges, 0);
        Assert.Null(resetAt);
        Assert.Null(window);
    }

    [Theory]
    [InlineData(60, -1)]            // exact minimum window, exact lower age bound
    [InlineData(31_622_400, 366)]   // exact maximum window, exact upper age bound
    public void ValuesOnTheAcceptedBounds_AreEmittedUnclamped(int windowSeconds, int offsetDays)
    {
        var mapped = _mapper.Map([Usage(gauges:
        [
            new UsageGauge("primary", "Primary", null, 42,
                FixedUtcNow.AddDays(offsetDays).UtcDateTime, null, windowSeconds),
        ])], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var (resetAt, window) = ResetPairOf(
            document.RootElement.GetProperty("providers")[0].GetProperty("gauges"), 0);
        Assert.NotNull(resetAt);
        Assert.Equal(windowSeconds, window);
    }

    // ---------------------------------------------------------------- provider sources

    [Fact]
    public void Codex_MapsLimitWindowSecondsThroughToTheWire()
    {
        // The wham API states each window's length outright; epoch chosen inside
        // the accepted range around FixedUtcNow.
        long resetEpochSeconds = FixedUtcNow.AddHours(4).ToUnixTimeSeconds();
        var response = new CodexUsageResponse
        {
            PlanType = "pro",
            RateLimit = new CodexUsageRateLimit
            {
                PrimaryWindow = Window(usedPercent: 40, limitWindowSeconds: 18_000, resetEpochSeconds),
                SecondaryWindow = Window(usedPercent: 10, limitWindowSeconds: 604_800, resetEpochSeconds),
            },
            AdditionalRateLimits =
            [
                new CodexAdditionalUsageRateLimit
                {
                    MeteredFeature = "codex_bengalfox",

                    // A window whose limit_window_seconds is absent (deserialises
                    // as 0) must publish no pair — even though it has a reset time.
                    RateLimit = new CodexUsageRateLimit
                    {
                        PrimaryWindow = Window(usedPercent: 0, limitWindowSeconds: 0, resetEpochSeconds),
                    },
                },
            ],
        };

        var gauges = CodexProvider.BuildGauges(response, displayName: "Codex");
        var mapped = _mapper.Map([Usage(id: "codex", gauges: gauges)], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var provider = document.RootElement.GetProperty("providers")[0];

        var primary = provider.GetProperty("gauges")[0];
        Assert.Equal("codex-primary", primary.GetProperty("id").GetString());
        Assert.Equal(18_000, primary.GetProperty("resetWindowSeconds").GetInt32());
        var expectedReset = DateTimeOffset.FromUnixTimeSeconds(resetEpochSeconds);
        Assert.Equal(expectedReset, DateTimeOffset.Parse(
            primary.GetProperty("resetAt").GetString()!,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind));

        var weekly = provider.GetProperty("gauges")[1];
        Assert.Equal("codex-weekly", weekly.GetProperty("id").GetString());
        Assert.Equal(604_800, weekly.GetProperty("resetWindowSeconds").GetInt32());

        var spark = provider.GetProperty("gauges")[2];
        Assert.Equal("codex-spark", spark.GetProperty("id").GetString());
        var (resetAt, window) = ResetPairOf(provider.GetProperty("gauges"), 2);
        Assert.Null(resetAt);
        Assert.Null(window);    }

    [Fact]
    public void Claude_MapsItsNamedWindowsThroughToTheWire()
    {
        // Anthropic's data declares these windows itself (five_hour / seven_day /
        // kind "weekly_scoped"), so the windows below transcribe the source, and
        // the wire values must match exactly.
        var snapshot = new ClaudeUsageSnapshot(
            ObservedAt: new DateTime(2026, 9, 17, 2, 0, 0),
            SourceLabel: "test",
            SourcePath: "test.json",
            FiveHourPercentUsed: 20,
            FiveHourResetAt: new DateTime(2026, 9, 17, 8, 0, 0, DateTimeKind.Utc),
            SevenDayPercentUsed: 30,
            SevenDayResetAt: new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc),
            ScopedLimits:
            [
                new ClaudeUsageLimit
                {
                    Kind = "weekly_scoped",
                    Percent = 10,
                    ResetsAt = "2026-09-20T08:00:00+00:00",
                    Scope = new ClaudeLimitScope
                    {
                        Model = new ClaudeLimitScopeModel { DisplayName = "Fable" },
                    },
                },
            ],
            ResetTimesAreApproximate: false);

        var gauges = ClaudeProvider.BuildGauges(snapshot, costData: null, displayName: "Claude Code");
        var mapped = _mapper.Map([Usage(id: "claude", gauges: gauges)], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var wireGauges = document.RootElement.GetProperty("providers")[0].GetProperty("gauges");

        Assert.Equal(3, wireGauges.GetArrayLength());

        var fiveHour = wireGauges[0];
        Assert.Equal("claude-5h", fiveHour.GetProperty("id").GetString());
        Assert.Equal(ClaudeProvider.FiveHourWindowSeconds, fiveHour.GetProperty("resetWindowSeconds").GetInt32());
        Assert.Equal(TimeSpan.Zero, ParsedResetOffset(fiveHour));

        var weekly = wireGauges[1];
        Assert.Equal("claude-weekly", weekly.GetProperty("id").GetString());
        Assert.Equal(ClaudeProvider.SevenDayWindowSeconds, weekly.GetProperty("resetWindowSeconds").GetInt32());

        var scoped = wireGauges[2];
        Assert.Equal("claude-weekly-fable", scoped.GetProperty("id").GetString());
        Assert.Equal(ClaudeProvider.SevenDayWindowSeconds, scoped.GetProperty("resetWindowSeconds").GetInt32());
        Assert.Equal(TimeSpan.Zero, ParsedResetOffset(scoped));
    }

    [Fact]
    public void Google_MapsItsDeclaredWindowBucketsThroughToTheWire_AndLeavesUnknownNamesBlank()
    {
        var resetUtc = new DateTime(2026, 9, 18, 0, 0, 0, DateTimeKind.Utc);
        var group = new GoogleQuotaGroup("Gemini Models",
        [
            new GoogleQuotaBucket("gemini-5h", "5h", "5-Hour Limit", 0.6, resetUtc, null, false),
            new GoogleQuotaBucket("gemini-weekly", "weekly", "Weekly Limit", 0.7, resetUtc, null, false),

            // A window name we do not recognise means the field said nothing we
            // can read — neither field may travel.
            new GoogleQuotaBucket("gemini-fortnight", "fortnight", "Fortnight Limit", 0.9, resetUtc, null, false),
        ]);

        var gauges = GoogleProvider.BuildWindowedGauges(group).ToList();
        var mapped = _mapper.Map([Usage(id: "google", gauges: gauges)], FixedUtcNow);

        using var document = JsonDocument.Parse(mapped.Json);
        var wireGauges = document.RootElement.GetProperty("providers")[0].GetProperty("gauges");
        Assert.Equal(3, wireGauges.GetArrayLength());

        Assert.Equal(18_000, wireGauges[0].GetProperty("resetWindowSeconds").GetInt32());
        Assert.Equal(TimeSpan.Zero, ParsedResetOffset(wireGauges[0]));

        Assert.Equal(604_800, wireGauges[1].GetProperty("resetWindowSeconds").GetInt32());

        var (resetAt, window) = ResetPairOf(wireGauges, 2);
        Assert.Null(resetAt);
        Assert.Null(window);
    }

    // ---------------------------------------------------------------- helpers

    private static CodexUsageWindow Window(int usedPercent, int limitWindowSeconds, long resetEpochSeconds) =>
        new()
        {
            UsedPercent = usedPercent,
            LimitWindowSeconds = limitWindowSeconds,
            ResetAtRaw = JsonSerializer.SerializeToElement(resetEpochSeconds),
        };

    /// <summary>Reads the (resetAt, resetWindowSeconds) pair off a wire gauge; nulls preserved.</summary>
    private static (string? ResetAt, int? WindowSeconds) ResetPairOf(JsonElement gaugesArray, int index)
    {
        var gauge = gaugesArray[index];
        var resetAt = gauge.GetProperty("resetAt");
        var window = gauge.GetProperty("resetWindowSeconds");
        return (
            resetAt.ValueKind == JsonValueKind.String ? resetAt.GetString() : null,
            window.ValueKind == JsonValueKind.Number ? window.GetInt32() : null);
    }

    private static TimeSpan ParsedResetOffset(JsonElement gauge) => DateTimeOffset.Parse(
        gauge.GetProperty("resetAt").GetString()!,
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.RoundtripKind).Offset;

    private static ProviderUsage Usage(
        string id = "codex",
        IReadOnlyList<UsageGauge>? gauges = null) => new()
    {
        ProviderId = id,
        DisplayName = id,
        State = ProviderState.Ok,
        Gauges = gauges ?? Array.Empty<UsageGauge>(),
    };
}
