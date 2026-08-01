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
    /// Infers when the current rolling window closes by finding where the series last fell.
    /// That fall is the window rolling over, so the window opened then and closes one window
    /// length later.
    /// </summary>
    /// <remarks>
    /// Deliberately conservative. It returns null — leaving the UI to say "current window" —
    /// rather than guessing whenever:
    /// <list type="bullet">
    /// <item><description>the window is idle (0% used), so no window is actually open;</description></item>
    /// <item><description>no reset is visible in the retained history;</description></item>
    /// <item><description>the projected reset has already passed, which means the desktop app
    /// was closed across a rollover and the history has a hole in it.</description></item>
    /// </list>
    /// The result is only ever as precise as the sampling interval (~5 minutes), and lands
    /// slightly late, because we can only see a reset at the first sample taken after it.
    /// </remarks>
    private static DateTime? DeriveReset(
        IReadOnlyList<ClaudePlanUsageSample> ordered,
        Func<ClaudePlanUsageValues, int?> selector,
        TimeSpan window,
        DateTime now)
    {
        var current = ordered.Count > 0 && ordered[^1].Usage is { } u ? selector(u) : null;
        if (current is null or <= 0) return null;

        for (var i = ordered.Count - 1; i > 0; i--)
        {
            if (ordered[i].Usage is not { } value || ordered[i - 1].Usage is not { } previous) continue;

            var after = selector(value);
            var before = selector(previous);
            if (after is null || before is null) continue;
            if (before.Value - after.Value < ResetDropThreshold) continue;

            var reset = ordered[i].ObservedAt + window;
            return reset > now ? reset : null;
        }

        return null;
    }

    private static int? Clamp(int? percent) =>
        percent.HasValue ? Math.Max(0, Math.Min(100, percent.Value)) : null;
}
