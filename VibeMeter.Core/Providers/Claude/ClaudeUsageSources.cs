using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// Which local Claude surface a reading came from. The numeric order IS the preference
/// order used by <see cref="ClaudeUsageSources.Merge"/> — lower wins.
/// </summary>
internal enum ClaudeUsageSource
{
    /// <summary>
    /// Anthropic's own usage endpoint, fetched over HTTP at the moment of the poll.
    /// Preferred above everything else, because it is the only source that is fresh by
    /// construction rather than by luck: it carries the same fields as the CLI cache, and
    /// its observation time is the fetch itself.
    /// </summary>
    /// <remarks>
    /// Both file sources describe a reading some other program chose to write. The CLI
    /// cache is only rewritten on an explicit <c>/usage</c> or a limit-approaching warning
    /// — measured on a Linux box, 2h10m of continuous heavy Claude Code use left it
    /// untouched for over a day — and the desktop history samples at a 30-minute median.
    /// Asking the API removes the dependency on either program having run recently.
    /// </remarks>
    LiveApi = 0,

    /// <summary>
    /// The Claude Code CLI's <c>usage_cache.json</c>. Preferred over every file source: it
    /// is the only one carrying exact percentages, verbatim reset timestamps and
    /// model-scoped weekly limits.
    /// </summary>
    CliCache = 1,

    /// <summary>
    /// A snapshot that did not declare its surface (only hand-built ones do). Ranked below
    /// the CLI cache — nothing lets us claim it is as rich — but above a source we know to
    /// be coarsely sampled.
    /// </summary>
    Unknown = 2,

    /// <summary>
    /// The Claude desktop app's <c>plan-usage-history.json</c>. The fallback: two
    /// percentages, no reset times, and a sampling cadence measured in tens of minutes.
    /// </summary>
    DesktopHistory = 3,
}

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
/// <param name="Source">
/// Which surface supplied the figures. Recorded so callers — the provider, the publishing
/// side, the log — can say which one won rather than inferring it from the label.
/// </param>
/// <param name="SamplingInterval">
/// How often this surface writes a reading, when it writes on a cadence rather than on
/// demand. Null for a source rewritten on use (the CLI cache), whose age is simply its age.
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
    bool ResetTimesAreApproximate,
    ClaudeUsageSource Source = ClaudeUsageSource.Unknown,
    TimeSpan? SamplingInterval = null)
{
    /// <summary>True when there is at least one figure worth painting a gauge for.</summary>
    public bool HasAnyUsage =>
        FiveHourPercentUsed.HasValue || SevenDayPercentUsed.HasValue || ScopedLimits.Count > 0;

    /// <summary>
    /// How old this reading may get before it stops describing "now" — judged by the source's
    /// own cadence, not by one tolerance applied to everything.
    /// </summary>
    /// <remarks>
    /// A sampled source is not stale merely for being between samples: at the desktop app's
    /// measured 30-minute median, a 25-minute-old reading is the freshest that surface has
    /// ever been able to offer. It only means the app has stopped once several samples in a
    /// row have gone missing. A source rewritten on demand has no cadence to be between, so
    /// it gets the flat allowance instead.
    /// </remarks>
    public TimeSpan MeaningfulFor => SamplingInterval is { } interval
        ? interval * ClaudeUsageSources.MissedSampleAllowance
        : ClaudeUsageSources.UnsampledSourceAllowance;

    /// <summary>True when this reading is still current by its own source's standard.</summary>
    public bool IsCurrentAt(DateTime now) => now - ObservedAt <= MeaningfulFor;
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
/// <b>Claude desktop app</b> — <c>plan-usage-history.json</c> under
/// <see cref="Environment.SpecialFolder.ApplicationData"/>. That folder is
/// <c>AppData\Roaming</c> on Windows but <c>~/.config</c> on Linux and
/// <c>~/Library/Application Support</c> on macOS, so this source is <b>live on every
/// platform</b> — not Windows-only, however much the old <c>%APPDATA%</c> shorthand
/// suggested otherwise. A rolling array of samples holding only the two percentages; reset
/// times have to be inferred from where the series drops.
/// </description></item>
/// </list>
/// <para>
/// <b>Cadence.</b> The desktop history was long documented here as "~5-minutely". Measured
/// over 106 consecutive real samples spanning a week, the gap between samples has a
/// <b>median of 30 minutes</b> (mean 93, min 10.6, max 840) and exceeds 20 minutes in 56% of
/// cases — roughly six times slower than that claim. That is why
/// <see cref="ClaudeUsageSnapshot.MeaningfulFor"/> judges this source against its own
/// cadence rather than against a live source's tolerance.
/// </para>
/// <para>
/// <b>Selection.</b> Both files describe the same subscription pool, so either is a valid
/// read — but they are not equally good, and recency alone cannot express that. We prefer
/// the CLI cache whenever it carries any usage at all, even when the desktop history holds a
/// newer sample, and fall back to the desktop history only when the CLI cache is absent,
/// unreadable, or carries no figures. Reset times the winner lacks are still backfilled from
/// the loser, provided they are still in the future.
/// </para>
/// </remarks>
internal static class ClaudeUsageSources
{
    private static readonly string HomePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private const string CliSourceLabel = "Claude Code CLI cache";
    private const string DesktopSourceLabel = "Claude desktop app history";
    internal const string LiveSourceLabel = "Anthropic usage API";

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
    /// How often the desktop app actually writes a sample, measured rather than assumed:
    /// the median gap across 106 consecutive samples spanning a week. Not the mean (93 min),
    /// which one 14-hour outage drags far off the typical case.
    /// </summary>
    internal static readonly TimeSpan DesktopSamplingInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How many samples a cadenced source may miss before we stop reading it as "the
    /// current state, between samples" and start reading it as "the app has stopped".
    /// Six intervals is three hours for the desktop history: comfortably past the observed
    /// spread of normal gaps, well short of the 14-hour outage in the same data.
    /// </summary>
    internal const int MissedSampleAllowance = 6;

    /// <summary>
    /// The same allowance for a source with no cadence at all. The CLI cache is rewritten
    /// whenever Claude Code runs, so its age is just its age; six hours is the point past
    /// which a figure is worth flagging to the user rather than quietly showing.
    /// </summary>
    internal static readonly TimeSpan UnsampledSourceAllowance = TimeSpan.FromHours(6);

    /// <summary>
    /// Every path we look in, in preference order — used both for reading and for telling
    /// the user where we looked when nothing was found.
    /// </summary>
    public static IReadOnlyList<string> CandidatePaths => new[] { CliCachePath, DesktopHistoryPath };

    /// <summary>
    /// Resolves a file inside the Claude config directory, honouring <c>CLAUDE_CONFIG_DIR</c>
    /// — which relocates the whole <c>~/.claude</c> tree for users who keep it off the
    /// profile drive.
    /// </summary>
    /// <remarks>
    /// One helper rather than one rule per file. The usage cache and the OAuth credential
    /// live side by side in that directory, so they must relocate together; two independent
    /// copies of this rule would eventually disagree.
    /// </remarks>
    internal static string ConfigFile(string fileName)
    {
        var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        return string.IsNullOrWhiteSpace(configDir)
            ? Path.Combine(HomePath, ".claude", fileName)
            : Path.Combine(configDir.Trim(), fileName);
    }

    /// <summary>The CLI's usage cache.</summary>
    public static string CliCachePath => ConfigFile("usage_cache.json");

    /// <summary>
    /// The desktop app's sampled plan-usage history.
    /// </summary>
    /// <remarks>
    /// <see cref="Environment.SpecialFolder.ApplicationData"/> is NOT Windows-only. It is
    /// <c>AppData\Roaming</c> on Windows, <c>~/.config</c> on Linux and
    /// <c>~/Library/Application Support</c> on macOS, so this source is live on every
    /// platform we run on. Spelling it <c>%APPDATA%</c> here once hid a Linux-only
    /// symptom for weeks.
    /// </remarks>
    public static string DesktopHistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Claude", "plan-usage-history.json");

    /// <summary>True when at least one usage source exists on disk.</summary>
    public static bool AnyExists() => CandidatePaths.Any(File.Exists);

    /// <summary>
    /// Reads every available source and returns the best snapshot, or null when no source
    /// exists or none could be parsed.
    /// </summary>
    public static Task<ClaudeUsageSnapshot?> ReadBestAsync() =>
        ReadBestAsync(live: null);

    /// <summary>
    /// Merges an already-fetched live reading with whatever the local files hold. The live
    /// reading outranks both files and so wins whenever it is present; pass null when there
    /// was no credential, the credential had lapsed, or the call did not succeed, and the
    /// selection falls back to exactly what it chose before this source existed.
    /// </summary>
    /// <remarks>
    /// The fetch itself belongs to the provider, which owns the credential and the
    /// user-facing wording for a lapsed sign-in. This class stays a merger of readings.
    /// </remarks>
    public static Task<ClaudeUsageSnapshot?> ReadBestAsync(ClaudeUsageSnapshot? live) =>
        ReadBestAsync(live, CliCachePath, DesktopHistoryPath);

    /// <summary>
    /// Reads a named pair of files rather than the ones this machine happens to have.
    /// A null path means "that surface is not installed here". Internal so tests can drive
    /// the real readers and the real selection over captured fixtures without needing a
    /// Claude install on the build agent; production always calls the parameterless
    /// overload.
    /// </summary>
    internal static Task<ClaudeUsageSnapshot?> ReadBestAsync(
        string? cliCachePath,
        string? desktopHistoryPath) =>
        ReadBestAsync(live: null, cliCachePath, desktopHistoryPath);

    /// <summary>
    /// The full selection: an optional live reading plus a named pair of files.
    /// </summary>
    internal static async Task<ClaudeUsageSnapshot?> ReadBestAsync(
        ClaudeUsageSnapshot? live,
        string? cliCachePath,
        string? desktopHistoryPath)
    {
        var snapshots = new List<ClaudeUsageSnapshot>();

        if (live is not null)
            snapshots.Add(live);
        if (cliCachePath is not null && await ReadCliCacheAsync(cliCachePath) is { } cli)
            snapshots.Add(cli);
        if (desktopHistoryPath is not null && await ReadDesktopHistoryAsync(desktopHistoryPath) is { } desktop)
            snapshots.Add(desktop);

        return Merge(snapshots);
    }

    // --- Source 0: Anthropic's usage endpoint --------------------------------------------

    /// <summary>
    /// Wraps a usage payload fetched live into a snapshot, or returns null when the payload
    /// was absent.
    /// </summary>
    /// <param name="data">The deserialised response. It is the same shape as the CLI
    /// cache's <c>data</c> member, because that cache stores the response verbatim.</param>
    /// <param name="observedAt">
    /// The moment of the fetch. A live reading observes the value now — which is precisely
    /// why this source fixes the staleness problem the file sources have.
    /// </param>
    internal static ClaudeUsageSnapshot? FromLiveApi(ClaudeUsageData? data, DateTime observedAt)
    {
        if (data is null) return null;

        return new ClaudeUsageSnapshot(
            ObservedAt: observedAt,
            SourceLabel: LiveSourceLabel,
            SourcePath: ClaudeApiClient.UsageUrl,
            Source: ClaudeUsageSource.LiveApi,

            // Not a sampled source: we asked, and it answered. There is no cadence to be
            // between, so its age is simply its age.
            SamplingInterval: null,
            FiveHourPercentUsed: data.FiveHour?.UsedPercent,
            FiveHourResetAt: data.FiveHour?.ResetAt,
            SevenDayPercentUsed: data.SevenDay?.UsedPercent,
            SevenDayResetAt: data.SevenDay?.ResetAt,
            ScopedLimits: ScopedLimitsOf(data),
            ResetTimesAreApproximate: false);
    }

    /// <summary>
    /// The model-scoped weekly limits worth painting a gauge for — those that name the
    /// model they apply to.
    /// </summary>
    private static List<ClaudeUsageLimit> ScopedLimitsOf(ClaudeUsageData data) =>
        data.Limits?
            .Where(l => l.Kind == "weekly_scoped" && !string.IsNullOrWhiteSpace(l.Scope?.Model?.DisplayName))
            .ToList() ?? new List<ClaudeUsageLimit>();

    /// <summary>
    /// Picks the best snapshot by SOURCE, not by recency, and backfills reset times it is
    /// missing from the others. Percentages are never mixed across sources — a blended
    /// reading would be wrong for both.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The rule.</b> Discard any snapshot with no usage figures; of what remains, take
    /// the one whose <see cref="ClaudeUsageSnapshot.Source"/> ranks highest
    /// (<see cref="ClaudeUsageSource.LiveApi"/> first, then
    /// <see cref="ClaudeUsageSource.CliCache"/>), breaking a tie within one source by
    /// recency. So a live reading wins whenever one could be fetched; failing that the CLI
    /// cache wins whenever it is usable at all, even against a newer desktop sample; and the
    /// desktop history is used only when both are absent, unreadable, or carry no figures.
    /// </para>
    /// <para>
    /// <b>Why not recency.</b> Ordering purely by <c>ObservedAt</c> cannot express what this
    /// file already knows — that the CLI cache is the richer source. On a Linux box where
    /// the CLI had stopped rewriting its cache, a desktop sample a day newer won every
    /// merge; its own timestamp then never advanced, so the merged reading sat permanently
    /// past the agent's 20-minute freshness gate and the provider vanished from every
    /// published snapshot, silently, at [warn]. The same trap exists on Windows: at the
    /// desktop history's measured 30-minute median cadence, its sample is older than that
    /// gate 56% of the time. Windows escapes only because its CLI cache stays fresh and
    /// happens to win on recency — luck, not design.
    /// </para>
    /// <para>
    /// <b>The trade-off.</b> Preferring rank over recency means we can now return a CLI
    /// reading that is genuinely older than an available desktop one. That is deliberate,
    /// and it is the honest answer: both readings then carry their true
    /// <see cref="ClaudeUsageSnapshot.ObservedAt"/>, and the caller decides what to do about
    /// age — the UI flags it, and the agent's freshness gate omits the provider rather than
    /// publishing day-old figures stamped "now", which the collection API's newest-first
    /// merge would let mask a fresher reading from another machine. Choosing the richer
    /// source never makes that gate more permissive; it only stops a thin, slowly-sampled
    /// source displacing a detailed one.
    /// </para>
    /// </remarks>
    internal static ClaudeUsageSnapshot? Merge(IReadOnlyList<ClaudeUsageSnapshot> snapshots)
    {
        var usable = snapshots.Where(s => s.HasAnyUsage).ToList();
        if (usable.Count == 0) return null;

        // Rank first, recency only as a tie-break within one source. Today there is at most
        // one snapshot per source, so the tie-break never fires; it is here so that adding a
        // third surface cannot make the choice depend on list order.
        var winner = usable
            .OrderBy(s => (int)s.Source)
            .ThenByDescending(s => s.ObservedAt)
            .First();
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

    private static async Task<ClaudeUsageSnapshot?> ReadCliCacheAsync(string path)
    {
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

        return new ClaudeUsageSnapshot(
            ObservedAt: ClaudeJson.ParseIso(cache?.Timestamp) ?? File.GetLastWriteTime(path),
            SourceLabel: CliSourceLabel,
            SourcePath: path,
            Source: ClaudeUsageSource.CliCache,

            // Written whenever Claude Code runs, not on a timer, so there is no cadence to
            // be between: this reading's age means exactly what it says.
            SamplingInterval: null,
            FiveHourPercentUsed: data.FiveHour?.UsedPercent,
            FiveHourResetAt: data.FiveHour?.ResetAt,
            SevenDayPercentUsed: data.SevenDay?.UsedPercent,
            SevenDayResetAt: data.SevenDay?.ResetAt,
            ScopedLimits: ScopedLimitsOf(data),
            ResetTimesAreApproximate: false);
    }

    // --- Source 2: the desktop app's sampled history -------------------------------------

    private static async Task<ClaudeUsageSnapshot?> ReadDesktopHistoryAsync(string path)
    {
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
            Source: ClaudeUsageSource.DesktopHistory,

            // Sampled, so being a little behind is its normal state rather than a fault.
            SamplingInterval: DesktopSamplingInterval,
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
