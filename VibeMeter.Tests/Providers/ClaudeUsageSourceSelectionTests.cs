using VibeMeter.Providers.Claude;
using Xunit;

namespace VibeMeter.Tests.Providers;

/// <summary>
/// Which local Claude surface supplies the figures when more than one is present.
/// </summary>
/// <remarks>
/// <para>
/// The regression these pin was found on a Linux box ("Linux"), where the provider was
/// omitted from every published snapshot by the agent's 20-minute freshness gate — for
/// weeks, silently, at [warn]. Both local sources were valid; the selection between them
/// was not.
/// </para>
/// <para>
/// <see cref="ClaudeUsageSources.DesktopHistoryPath"/> is built from
/// <c>SpecialFolder.ApplicationData</c>, which is <c>AppData\Roaming</c> on Windows but
/// <c>~/.config</c> on Linux — so the "desktop app" source is live there. Its newest sample
/// was a day newer than the CLI cache, it therefore won a merge ordered purely by
/// <c>ObservedAt</c>, and its own timestamp never advanced again. The merged reading aged
/// past the gate and stayed past it.
/// </para>
/// <para>
/// The fixtures are the real captured files (organisation redacted, free text replaced
/// with <c>&lt;str:N&gt;</c> placeholders — key names and every date-shaped value are intact).
/// They are driven through the production readers, not hand-built snapshots, so a change to
/// parsing cannot quietly invalidate them.
/// </para>
/// </remarks>
public sealed class ClaudeUsageSourceSelectionTests : IDisposable
{
    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Providers", "Claude", "Fixtures");

    private static string Fixture(string name) => Path.Combine(FixtureDirectory, name);

    private static string LinuxCliCache => Fixture("linux-usage_cache.json");
    private static string LinuxDesktopHistory => Fixture("linux-plan-usage-history.json");
    private static string WindowsCliCacheWithBom => Fixture("windows-usage_cache-bom.json");

    /// <summary>Linux's CLI cache: <c>"timestamp": "2026-09-17T02:07:48.544Z"</c>.</summary>
    private static readonly DateTime LinuxCliObservedAt =
        new DateTimeOffset(2026, 9, 17, 2, 7, 48, 544, TimeSpan.Zero).LocalDateTime;

    /// <summary>Linux's newest desktop sample: <c>"t": 1789720448590</c>.</summary>
    private static readonly DateTime LinuxDesktopObservedAt =
        DateTimeOffset.FromUnixTimeMilliseconds(1789720448590).LocalDateTime;

    /// <summary>Scratch space for the degenerate caches, which are easier built than stored.</summary>
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "vibemeter-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    private string WriteScratch(string name, string content)
    {
        Directory.CreateDirectory(_scratch);
        var path = Path.Combine(_scratch, name);
        File.WriteAllText(path, content);
        return path;
    }

    // ------------------------------------------------------------------ the regression

    [Fact]
    public async Task Linux_TheCliCacheWins_EvenThoughTheDesktopSampleIsADayNewer()
    {
        // The premise, asserted rather than assumed: the desktop sample really is the
        // newer of the two, which is exactly why ordering by ObservedAt chose it.
        Assert.True(
            LinuxDesktopObservedAt > LinuxCliObservedAt,
            "fixture premise: the desktop sample must be newer than the CLI cache");

        var snapshot = await ClaudeUsageSources.ReadBestAsync(
            cliCachePath: LinuxCliCache,
            desktopHistoryPath: LinuxDesktopHistory);

        Assert.NotNull(snapshot);

        // The CLI cache is the richer source — exact percentages, exact reset timestamps,
        // model-scoped weekly limits — and must win whenever it is usable at all.
        Assert.Equal("Claude Code CLI cache", snapshot!.SourceLabel);
        Assert.Equal(ClaudeUsageSource.CliCache, snapshot.Source);
        Assert.Equal(LinuxCliObservedAt, snapshot.ObservedAt);
        Assert.Equal(14, snapshot.FiveHourPercentUsed);
        Assert.Equal(77, snapshot.SevenDayPercentUsed);
        Assert.False(snapshot.ResetTimesAreApproximate);
    }

    [Fact]
    public async Task Linux_TheReadingIsStillDroppedByTheGate_BecauseNeitherSourceIsFresh()
    {
        // Documents the rest of the mechanism, and the limit of this fix. Choosing the
        // right source does not conjure fresh data: on Linux the CLI cache had not been
        // rewritten for a day either, so the provider is still — correctly — omitted. The
        // fix stops a thin source DISPLACING a rich one; it does not, and must not, relax
        // the gate, because publishing day-old figures stamped "now" would mask the good
        // machine's reading under the server's newest-first merge.
        var snapshot = await ClaudeUsageSources.ReadBestAsync(
            cliCachePath: LinuxCliCache,
            desktopHistoryPath: LinuxDesktopHistory);

        var justAfterTheNewestSample = LinuxDesktopObservedAt.AddMinutes(21);
        Assert.True(justAfterTheNewestSample - snapshot!.ObservedAt > TimeSpan.FromMinutes(20));
    }

    // ------------------------------------------------------------------ the fallback path

    [Fact]
    public async Task CliCacheAbsent_TheDesktopHistoryIsUsed()
    {
        var snapshot = await ClaudeUsageSources.ReadBestAsync(
            cliCachePath: null,
            desktopHistoryPath: LinuxDesktopHistory);

        Assert.NotNull(snapshot);
        Assert.Equal(ClaudeUsageSource.DesktopHistory, snapshot!.Source);
        Assert.Equal("Claude desktop app history", snapshot.SourceLabel);
        Assert.Equal(LinuxDesktopObservedAt, snapshot.ObservedAt);

        // The newest sample in the captured file: { "fh": 27, "sd": 26 }.
        Assert.Equal(27, snapshot.FiveHourPercentUsed);
        Assert.Equal(26, snapshot.SevenDayPercentUsed);

        // It carries no reset timestamps of its own, so anything shown is an inference.
        Assert.True(snapshot.ResetTimesAreApproximate);
    }

    [Fact]
    public async Task CliCachePathPointsNowhere_TheDesktopHistoryIsUsed()
    {
        // The realistic desktop-only machine: the path is computed but no file is there.
        var snapshot = await ClaudeUsageSources.ReadBestAsync(
            cliCachePath: Path.Combine(_scratch, "does-not-exist", "usage_cache.json"),
            desktopHistoryPath: LinuxDesktopHistory);

        Assert.Equal(ClaudeUsageSource.DesktopHistory, snapshot!.Source);
    }

    [Fact]
    public async Task BothSourcesAbsent_ReturnsNull()
    {
        var snapshot = await ClaudeUsageSources.ReadBestAsync(
            cliCachePath: null,
            desktopHistoryPath: null);

        Assert.Null(snapshot);
    }

    // ------------------------------------------- a CLI cache that is genuinely unusable

    [Fact]
    public async Task CliCacheIsMalformed_TheDesktopHistoryIsUsed()
    {
        // A half-written cache must not sink the provider: the desktop history may still
        // have perfectly good figures.
        var torn = WriteScratch("usage_cache.json", "{ \"timestamp\": \"2026-09-18T08:3");

        var snapshot = await ClaudeUsageSources.ReadBestAsync(torn, LinuxDesktopHistory);

        Assert.Equal(ClaudeUsageSource.DesktopHistory, snapshot!.Source);
        Assert.Equal(27, snapshot.FiveHourPercentUsed);
    }

    [Fact]
    public async Task CliCacheParsesButCarriesNoFigures_TheDesktopHistoryIsUsed()
    {
        // Well-formed, newer than the desktop sample, and empty. "Usable" has to mean
        // "has a figure in it", not merely "parsed" — otherwise preferring the CLI cache
        // would trade one silent blackout for another.
        var empty = WriteScratch("usage_cache.json", """
            {
              "timestamp": "2026-09-18T08:40:00.000Z",
              "data": { "five_hour": null, "seven_day": null, "limits": [] }
            }
            """);

        var snapshot = await ClaudeUsageSources.ReadBestAsync(empty, LinuxDesktopHistory);

        Assert.Equal(ClaudeUsageSource.DesktopHistory, snapshot!.Source);
        Assert.Equal(27, snapshot.FiveHourPercentUsed);
    }

    [Fact]
    public async Task CliCacheCarriesNoFigures_AndNoDesktopHistoryExists_ReturnsNull()
    {
        var empty = WriteScratch("usage_cache.json", """
            { "timestamp": "2026-09-18T08:40:00.000Z", "data": { "limits": [] } }
            """);

        Assert.Null(await ClaudeUsageSources.ReadBestAsync(empty, desktopHistoryPath: null));
    }

    // ------------------------------------------------- the machine that already worked

    [Fact]
    public async Task WindowsShapedCacheWithABom_StillParsesAndStillWins()
    {
        // Windows writes usage_cache.json with a UTF-8 BOM and a top-level "timestamp".
        // Rock publishes today because that cache stays fresh and wins; the fix must not
        // cost the machine that works.
        Assert.Equal(
            new byte[] { 0xEF, 0xBB, 0xBF },
            (await File.ReadAllBytesAsync(WindowsCliCacheWithBom))[..3]);

        var snapshot = await ClaudeUsageSources.ReadBestAsync(
            cliCachePath: WindowsCliCacheWithBom,
            desktopHistoryPath: LinuxDesktopHistory);

        Assert.NotNull(snapshot);
        Assert.Equal(ClaudeUsageSource.CliCache, snapshot!.Source);
        Assert.Equal(41, snapshot.FiveHourPercentUsed);
        Assert.Equal(63, snapshot.SevenDayPercentUsed);

        // Verbatim reset timestamps and the model-scoped weekly limit — the detail that
        // makes this source the richer one, and that the desktop history simply lacks.
        Assert.False(snapshot.ResetTimesAreApproximate);
        Assert.Equal(
            new DateTimeOffset(2026, 9, 18, 10, 0, 0, TimeSpan.Zero).LocalDateTime,
            snapshot.FiveHourResetAt);
        var scoped = Assert.Single(snapshot.ScopedLimits);
        Assert.Equal("Opus", scoped.Scope?.Model?.DisplayName);
    }

    // ------------------------------------------------------------- the rule, in isolation

    [Fact]
    public void Merge_PrefersTheCliCache_OverANewerDesktopSample()
    {
        var chosen = ClaudeUsageSources.Merge(
        [
            Snapshot(ClaudeUsageSource.DesktopHistory, observedAt: DateTime.Now),
            Snapshot(ClaudeUsageSource.CliCache, observedAt: DateTime.Now.AddDays(-1)),
        ]);

        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
    }

    [Fact]
    public void Merge_FallsBackToTheDesktopHistory_WhenTheCliCacheHasNoFigures()
    {
        var chosen = ClaudeUsageSources.Merge(
        [
            Snapshot(ClaudeUsageSource.CliCache, DateTime.Now, fiveHourPercentUsed: null),
            Snapshot(ClaudeUsageSource.DesktopHistory, DateTime.Now.AddHours(-2)),
        ]);

        Assert.Equal(ClaudeUsageSource.DesktopHistory, chosen!.Source);
    }

    [Fact]
    public void Merge_StillBackfillsAFutureResetTimeFromTheLosingSource()
    {
        // Ranking changed; backfill did not. A reset timestamp stays valid however old its
        // source is, right up until the window it describes closes.
        var futureReset = DateTime.Now.AddHours(3);
        var chosen = ClaudeUsageSources.Merge(
        [
            Snapshot(ClaudeUsageSource.CliCache, DateTime.Now, fiveHourResetAt: null),
            Snapshot(ClaudeUsageSource.DesktopHistory, DateTime.Now.AddHours(-2), fiveHourResetAt: futureReset),
        ]);

        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
        Assert.Equal(futureReset, chosen.FiveHourResetAt);
    }

    [Fact]
    public void Merge_OfNothingUsable_IsNull()
    {
        Assert.Null(ClaudeUsageSources.Merge([]));
        Assert.Null(ClaudeUsageSources.Merge(
            [Snapshot(ClaudeUsageSource.CliCache, DateTime.Now, fiveHourPercentUsed: null)]));
    }

    // --------------------------------------------------------------- cadence, per source

    [Fact]
    public async Task TheDesktopHistoryIsJudgedByItsOwnCadence_NotALiveSourcesTolerance()
    {
        var desktop = await ClaudeUsageSources.ReadBestAsync(null, LinuxDesktopHistory);

        // Its measured median gap is 30 minutes, so a reading 25 minutes old is the
        // freshest that surface has ever been able to offer — not a fault.
        Assert.Equal(TimeSpan.FromMinutes(30), desktop!.SamplingInterval);
        Assert.True(desktop.IsCurrentAt(desktop.ObservedAt.AddMinutes(25)));

        // Several missed samples in a row is a different matter: the app has stopped.
        Assert.False(desktop.IsCurrentAt(desktop.ObservedAt.AddHours(4)));
    }

    [Fact]
    public async Task TheCliCacheHasNoCadence_SoItKeepsTheFlatAllowance()
    {
        var cli = await ClaudeUsageSources.ReadBestAsync(WindowsCliCacheWithBom, null);

        // Rewritten whenever Claude Code runs, so there is no interval to be between.
        Assert.Null(cli!.SamplingInterval);
        Assert.True(cli.IsCurrentAt(cli.ObservedAt.AddHours(5)));
        Assert.False(cli.IsCurrentAt(cli.ObservedAt.AddHours(7)));
    }

    // ------------------------------------------------------------------------- helpers

    private static ClaudeUsageSnapshot Snapshot(
        ClaudeUsageSource source,
        DateTime observedAt,
        int? fiveHourPercentUsed = 10,
        DateTime? fiveHourResetAt = null) => new(
            ObservedAt: observedAt,
            SourceLabel: source.ToString(),
            SourcePath: $"{source}.json",
            FiveHourPercentUsed: fiveHourPercentUsed,
            FiveHourResetAt: fiveHourResetAt,
            SevenDayPercentUsed: null,
            SevenDayResetAt: null,
            ScopedLimits: [],
            ResetTimesAreApproximate: source == ClaudeUsageSource.DesktopHistory,
            Source: source,
            SamplingInterval: source == ClaudeUsageSource.DesktopHistory
                ? ClaudeUsageSources.DesktopSamplingInterval
                : null);
}
