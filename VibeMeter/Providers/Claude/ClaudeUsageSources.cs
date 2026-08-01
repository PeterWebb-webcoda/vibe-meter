using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// One provider-agnostic reading of Claude plan utilisation, normalised from whichever
/// local file happened to supply it.
/// </summary>
/// <param name="ObservedAt">When the underlying surface last refreshed these figures.</param>
/// <param name="SourceLabel">Human-readable surface name, for tooltips.</param>
/// <param name="SourcePath">The file the figures came from, for diagnostics.</param>
/// <param name="ResetTimesAreApproximate">
/// True when the reset times were inferred from a sampled history rather than reported
/// verbatim — they are then only accurate to the sampling interval.
/// </param>
internal sealed record ClaudeUsageSnapshot(
    DateTime ObservedAt,
    string SourceLabel,
    string SourcePath,
    int? FiveHourPercentUsed,
    DateTime? FiveHourResetAt,
    int? SevenDayPercentUsed,
    DateTime? SevenDayResetAt,
    IReadOnlyList<ClaudeUsageLimit> ScopedLimits,
    bool ResetTimesAreApproximate)
{
    /// <summary>True when there is at least one figure worth painting a gauge for.</summary>
    public bool HasAnyUsage =>
        FiveHourPercentUsed.HasValue || SevenDayPercentUsed.HasValue || ScopedLimits.Count > 0;
}

/// <summary>
/// Locates and reads every local file that can supply Claude plan utilisation, so the
/// provider works regardless of which Claude surface the user actually runs.
/// </summary>
/// <remarks>
/// <para>Two surfaces write usage locally, and a given PC may have either, both, or neither:</para>
/// <list type="bullet">
/// <item><description>
/// <b>Claude Code CLI</b> — <c>usage_cache.json</c> under the config directory. The richest
/// source: exact percentages, exact reset timestamps, and model-scoped weekly limits.
/// </description></item>
/// <item><description>
/// <b>Claude desktop app</b> — <c>%APPDATA%\Claude\plan-usage-history.json</c>. A rolling
/// array of ~5-minutely samples holding only the two percentages; reset times have to be
/// inferred from where the series drops.
/// </description></item>
/// </list>
/// <para>
/// Both describe the same subscription pool, so either is a valid read. We take whichever
/// observed the account most recently and backfill any reset time the winner lacks from the
/// other, provided that time is still in the future.
/// </para>
/// </remarks>
internal static class ClaudeUsageSources
{
    private static readonly string HomePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private const string CliSourceLabel = "Claude Code CLI cache";
    private const string DesktopSourceLabel = "Claude desktop app history";

    /// <summary>Rolling-window lengths, used to project a reset from an observed drop.</summary>
    private static readonly TimeSpan FiveHourWindow = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDayWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Smallest percentage-point fall we will treat as a window reset rather than sampling
    /// noise. Real resets drop to zero (or to whatever was consumed in the first minutes of
    /// the new window), so they clear this comfortably.
    /// </summary>
    private const int ResetDropThreshold = 5;

    /// <summary>
    /// Every path we look in, in preference order — used both for reading and for telling
    /// the user where we looked when nothing was found.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths => new[] { CliCachePath, DesktopHistoryPath };

    /// <summary>
    /// The CLI's usage cache. Honours <c>CLAUDE_CONFIG_DIR</c>, which relocates the whole
    /// <c>~/.claude</c> tree for users who keep it off the profile drive.
    /// </summary>
    public static string CliCachePath
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(configDir)
                ? Path.Combine(HomePath, ".claude", "usage_cache.json")
                : Path.Combine(configDir.Trim(), "usage_cache.json");
        }
    }

    /// <summary>The desktop app's sampled plan-usage history.</summary>
    public static string DesktopHistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude", "plan-usage-history.json");

    /// <summary>True when at least one usage source exists on disk.</summary>
    public static bool AnyExists() => CandidatePaths.Any(File.Exists);

    /// <summary>
    /// Reads every available source and returns the best snapshot, or null when no source
    /// exists or none could be parsed.
    /// </summary>
    public static async Task<ClaudeUsageSnapshot?> ReadBestAsync()
    {
        var snapshots = new List<ClaudeUsageSnapshot>();

        if (await ReadCliCacheAsync() is { } cli) snapshots.Add(cli);
        if (await ReadDesktopHistoryAsync() is { } desktop) snapshots.Add(desktop);

        return Merge(snapshots);
    }

    /// <summary>
    /// Picks the most recently observed snapshot and backfills reset times it is missing
    /// from the others. Percentages are never mixed across sources — a blended reading
    /// would be wrong for both.
    /// </summary>
    internal static ClaudeUsageSnapshot? Merge(IReadOnlyList<ClaudeUsageSnapshot> snapshots)
    {
        var usable = snapshots.Where(s => s.HasAnyUsage).ToList();
        if (usable.Count == 0) return null;

        var winner = usable.OrderByDescending(s => s.ObservedAt).First();
        var now = DateTime.Now;

        foreach (var other in usable.Where(s => !ReferenceEquals(s, winner)))
        {
            // A reset timestamp stays valid no matter how old its source is, right up until
            // the window it describes closes — so "still in the future" is the only test
            // that matters here.
            if (winner.FiveHourResetAt is null && other.FiveHourResetAt > now)
                winner = winner with { FiveHourResetAt = other.FiveHourResetAt };

            if (winner.SevenDayResetAt is null && other.SevenDayResetAt > now)
                winner = winner with { SevenDayResetAt = other.SevenDayResetAt };
        }

        return winner;
    }

    // --- Source 1: the CLI's usage cache -------------------------------------------------

    private static async Task<ClaudeUsageSnapshot?> ReadCliCacheAsync()
    {
        var path = CliCachePath;
        if (!File.Exists(path)) return null;

        ClaudeUsageCacheFile? cache;
        try
        {
            await using var stream = File.OpenRead(path);
            cache = await JsonSerializer.DeserializeAsync<ClaudeUsageCacheFile>(stream);
        }
        catch
        {
            // A half-written or malformed cache must not sink the whole provider — the
            // desktop history may still have perfectly good figures.
            return null;
        }

        var data = cache?.Data;
        if (data is null) return null;

        var scoped = data.Limits?
            .Where(l => l.Kind == "weekly_scoped" && !string.IsNullOrWhiteSpace(l.Scope?.Model?.DisplayName))
            .ToList() ?? new List<ClaudeUsageLimit>();

        return new ClaudeUsageSnapshot(
            ObservedAt: ClaudeJson.ParseIso(cache?.Timestamp) ?? File.GetLastWriteTime(path),
            SourceLabel: CliSourceLabel,
            SourcePath: path,
            FiveHourPercentUsed: data.FiveHour?.UsedPercent,
            FiveHourResetAt: data.FiveHour?.ResetAt,
            SevenDayPercentUsed: data.SevenDay?.UsedPercent,
            SevenDayResetAt: data.SevenDay?.ResetAt,
            ScopedLimits: scoped,
            ResetTimesAreApproximate: false);
    }

    // --- Source 2: the desktop app's sampled history -------------------------------------

    private static async Task<ClaudeUsageSnapshot?> ReadDesktopHistoryAsync()
    {
        var path = DesktopHistoryPath;
        if (!File.Exists(path)) return null;

        ClaudePlanUsageHistoryFile? history;
        try
        {
            await using var stream = File.OpenRead(path);
            history = await JsonSerializer.DeserializeAsync<ClaudePlanUsageHistoryFile>(stream);
        }
        catch
        {
            return null;
        }

        var samples = history?.Samples;
        if (samples is null || samples.Count == 0) return null;

        var ordered = samples.OrderBy(s => s.TimestampMs).ToList();
        var latest = ordered[^1];
        if (latest.Usage is null) return null;

        // Samples carry the organisation they were taken under. Keep only the current one,
        // otherwise a user who has switched orgs would get a reset derived from the wrong
        // account's history.
        if (!string.IsNullOrWhiteSpace(latest.Org))
        {
            ordered = ordered.Where(s => s.Org == latest.Org).ToList();
        }

        var now = DateTime.Now;

        return new ClaudeUsageSnapshot(
            ObservedAt: latest.ObservedAt,
            SourceLabel: DesktopSourceLabel,
            SourcePath: path,
            FiveHourPercentUsed: Clamp(latest.Usage.FiveHour),
            FiveHourResetAt: DeriveReset(ordered, u => u.FiveHour, FiveHourWindow, now),
            SevenDayPercentUsed: Clamp(latest.Usage.SevenDay),
            SevenDayResetAt: DeriveReset(ordered, u => u.SevenDay, SevenDayWindow, now),
            // No scoped limits are recorded in this file. We deliberately do not borrow them
            // from a possibly-stale CLI cache: a wrong per-model gauge is worse than none.
            ScopedLimits: Array.Empty<ClaudeUsageLimit>(),
            ResetTimesAreApproximate: true);
    }

    /// <summary>
    /// Infers when the current window closes, by finding a past rollover and projecting the
    /// cadence forward.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A rollover shows up as the series falling — e.g. <c>fh</c> 99 → 0. Sampling only tells
    /// us it happened somewhere between two samples, but window boundaries land on the hour,
    /// so when exactly one hour mark falls inside that bracket we can recover the boundary
    /// exactly rather than to sampling precision. From there the cadence repeats every window
    /// length, so we step forward to the first boundary still ahead of us.
    /// </para>
    /// <para>
    /// Anchors that snapped to an hour are preferred over ones that did not, even if older:
    /// an exact anchor projected across several windows beats an imprecise recent one, since
    /// the cadence itself does not drift.
    /// </para>
    /// <para>
    /// It returns null — leaving the UI to say "current window" — rather than guess when no
    /// rollover survives in the retained history, when the only anchor is too old to trust,
    /// or when the projection contradicts the observed usage. A zero-utilisation reading is
    /// still eligible when it follows a recent rollover: that is the normal idle state during
    /// the newly opened window, and is exactly when the UI still needs to show its reset time.
    /// </para>
    /// </remarks>
    private static DateTime? DeriveReset(
        IReadOnlyList<ClaudePlanUsageSample> ordered,
        Func<ClaudePlanUsageValues, int?> selector,
        TimeSpan window,
        DateTime now)
    {
        var current = ordered.Count > 0 && ordered[^1].Usage is { } u ? selector(u) : null;
        if (current is null) return null;

        // Active windows benefit from the exact-hour anchor preference below. For an idle
        // window, however, the newest observed rollover is the only useful evidence; an older
        // exact anchor can otherwise win and make a valid fresh reset look stale.
        var anchor = current.Value > 0
            ? FindAnchor(ordered, selector)
            : FindMostRecentAnchor(ordered, selector);
        if (anchor is null) return null;

        // Once an idle window's last observed rollover is more than one window old, the
        // history cannot tell whether that window is still open or simply unused. Do not
        // invent a countdown in that case.
        if (current.Value <= 0 && now - anchor.Value > window) return null;

        // An anchor only stays useful while the cadence it pins is still recognisable. Well
        // beyond a handful of windows, any small error compounds and the history is likely to
        // have holes anyway.
        if (now - anchor.Value > MaxAnchorAge(window)) return null;

        var reset = anchor.Value;
        while (reset <= now) reset += window;

        // Sanity-check the projection against reality: the window it implies cannot have
        // started after usage we already observed inside it. If it did, the cadence is not
        // what we think it is, and no countdown beats a wrong one.
        var windowStart = reset - window;
        return windowStart <= FirstUsageInCurrentRun(ordered, selector) ? reset : null;
    }

    /// <summary>
    /// Locates the most recent rollover to project from, preferring one whose bracketing
    /// samples pin it to an exact hour.
    /// </summary>
    private static DateTime? FindAnchor(
        IReadOnlyList<ClaudePlanUsageSample> ordered,
        Func<ClaudePlanUsageValues, int?> selector)
    {
        DateTime? approximate = null;

        for (var i = ordered.Count - 1; i > 0; i--)
        {
            if (ordered[i].Usage is not { } value || ordered[i - 1].Usage is not { } previous) continue;

            var after = selector(value);
            var before = selector(previous);
            if (after is null || before is null) continue;
            if (before.Value - after.Value < ResetDropThreshold) continue;

            var from = ordered[i - 1].ObservedAt;
            var to = ordered[i].ObservedAt;

            if (SoleHourMarkBetween(from, to) is { } exact) return exact;

            // Keep the newest imprecise rollover as a fallback, but keep looking further back
            // for an exact one.
            approximate ??= to;
        }

        return approximate;
    }

    /// <summary>
    /// Locates the newest rollover, snapping it to an hour when the surrounding samples make
    /// that boundary unambiguous. Unlike <see cref="FindAnchor"/>, this deliberately does not
    /// prefer an older exact anchor: it is used only for a zero-utilisation window, where
    /// recency is more informative than cadence precision.
    /// </summary>
    private static DateTime? FindMostRecentAnchor(
        IReadOnlyList<ClaudePlanUsageSample> ordered,
        Func<ClaudePlanUsageValues, int?> selector)
    {
        for (var i = ordered.Count - 1; i > 0; i--)
        {
            if (ordered[i].Usage is not { } value || ordered[i - 1].Usage is not { } previous) continue;

            var after = selector(value);
            var before = selector(previous);
            if (after is null || before is null) continue;
            if (before.Value - after.Value < ResetDropThreshold) continue;

            var from = ordered[i - 1].ObservedAt;
            var to = ordered[i].ObservedAt;
            return SoleHourMarkBetween(from, to) ?? to;
        }

        return null;
    }

    /// <summary>
    /// Returns the single hour mark inside <c>(from, to]</c>, or null when the bracket spans
    /// none — or more than one, which would make the choice a guess.
    /// </summary>
    private static DateTime? SoleHourMarkBetween(DateTime from, DateTime to)
    {
        var mark = new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, from.Kind).AddHours(1);

        DateTime? only = null;
        for (; mark <= to; mark = mark.AddHours(1))
        {
            if (only is not null) return null;
            only = mark;
        }

        return only;
    }

    /// <summary>
    /// When the current unbroken run of non-zero readings began — the earliest moment we know
    /// the open window was already running.
    /// </summary>
    private static DateTime FirstUsageInCurrentRun(
        IReadOnlyList<ClaudePlanUsageSample> ordered,
        Func<ClaudePlanUsageValues, int?> selector)
    {
        var i = ordered.Count - 1;
        while (i > 0 && ordered[i - 1].Usage is { } previous && selector(previous) > 0) i--;
        return ordered[i].ObservedAt;
    }

    /// <summary>How stale an anchor may be before we stop projecting from it.</summary>
    private static TimeSpan MaxAnchorAge(TimeSpan window) => window * 6;

    private static int? Clamp(int? percent) =>
        percent.HasValue ? Math.Max(0, Math.Min(100, percent.Value)) : null;
}
