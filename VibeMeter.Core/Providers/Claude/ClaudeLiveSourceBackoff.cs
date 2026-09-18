using System;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// Decides whether the live usage source should be tried on this poll, given how its recent
/// attempts went, and remembers why not. One instance lives for the life of a
/// <see cref="ClaudeProvider"/>, in either host.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Anthropic's usage endpoint sits behind a per-address rate rule
/// at the edge (see <see cref="ClaudeApiClient"/>). Once tripped it answers <c>429</c>
/// with a <c>Retry-After</c> of roughly an hour to every request from the address — the
/// tray's, the agent's, and the statusline script that keeps <c>usage_cache.json</c>
/// fresh. A caller that treats that 429 as just another failure and comes straight back
/// on its next poll is part of what keeps the rule tripped, and it is doing so with no
/// chance of a reading. Staying away for exactly as long as the server asked costs
/// nothing: the local files are the fallback in any case.
/// </para>
/// <para>
/// <b>Two regimes.</b> A 429 pauses for the server's own <c>Retry-After</c>, clamped to a
/// sane range because a missing or absurd header must not turn into a permanent silence
/// or a permanent hammering. Any other failure — a timeout, a 5xx, no network, a rejected
/// credential — pauses on a doubling schedule from one minute, capped at a quarter of an
/// hour: short enough that a renewed sign-in or a returned network is noticed promptly,
/// long enough that this app is never the caller that turns a wobble into a storm. One
/// success clears everything.
/// </para>
/// <para>
/// <b>One reason per pause.</b> The explanation is composed once, when the pause starts,
/// and handed back unchanged on every paused poll. Hosts log a diagnostic when it
/// changes, so a stable string means one line for the whole pause rather than one a
/// minute.
/// </para>
/// <para>
/// <b>Not a clock owner.</b> Every method takes <c>now</c> so the schedule can be
/// asserted in tests without waiting for it.
/// </para>
/// </remarks>
internal sealed class ClaudeLiveSourceBackoff
{
    /// <summary>The floor for any pause, so a tiny or missing Retry-After cannot mean "hammer".</summary>
    internal static readonly TimeSpan MinimumPause = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The ceiling for a 429 pause. The observed Retry-After was 3505 s; anything longer is
    /// honoured up to an hour and then re-checked, because being early costs one 429 and a
    /// fresh pause, whereas trusting a day-long header would silence the source for a day
    /// on the server's say-so.
    /// </summary>
    internal static readonly TimeSpan MaximumRateLimitPause = TimeSpan.FromHours(1);

    /// <summary>What a 429 with no readable Retry-After earns.</summary>
    internal static readonly TimeSpan DefaultRateLimitPause = TimeSpan.FromMinutes(5);

    /// <summary>The ceiling for the doubling schedule after ordinary failures.</summary>
    internal static readonly TimeSpan MaximumFailurePause = TimeSpan.FromMinutes(15);

    /// <summary>Consecutive attempts that did not produce a reading. Zero after a success.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>When live fetches resume, or null when nothing is holding them back.</summary>
    public DateTimeOffset? PausedUntil { get; private set; }

    /// <summary>
    /// Why live fetches are paused, for the log: the failure's fixed description plus when
    /// the next attempt is due. Never a token, a header or a response body. Null when not
    /// paused.
    /// </summary>
    public string? PausedReason { get; private set; }

    /// <summary>
    /// The user-facing note that accompanied the failure that started the pause — a rejected
    /// sign-in — so the card can keep saying it while no new attempt is made. Null for a
    /// transient failure, which is not the user's to act on.
    /// </summary>
    public string? PausedNote { get; private set; }

    /// <summary>True while a previous failure says not to try yet.</summary>
    public bool IsPaused(DateTimeOffset now) => PausedUntil is { } until && now < until;

    /// <summary>
    /// Records a 429. Pauses for the server's Retry-After, clamped to
    /// [<see cref="MinimumPause"/>, <see cref="MaximumRateLimitPause"/>], or for
    /// <see cref="DefaultRateLimitPause"/> when the server did not say.
    /// </summary>
    /// <param name="detail">The client's token-free description of the response.</param>
    /// <returns>When live fetches resume.</returns>
    public DateTimeOffset RecordRateLimited(DateTimeOffset now, TimeSpan? retryAfter, string detail)
    {
        ConsecutiveFailures++;
        var until = now + Clamp(retryAfter ?? DefaultRateLimitPause, MinimumPause, MaximumRateLimitPause);

        PausedUntil = until;
        PausedNote = null;
        PausedReason = $"{detail}; live fetches paused until {until.ToLocalTime():HH:mm} so the limit can clear";
        return until;
    }

    /// <summary>
    /// Records any failure other than a 429. Pauses for one minute, doubling per consecutive
    /// failure, capped at <see cref="MaximumFailurePause"/>.
    /// </summary>
    /// <param name="detail">The client's token-free description of the failure.</param>
    /// <param name="note">The user-facing note to keep showing during the pause, if any.</param>
    /// <returns>When live fetches resume.</returns>
    public DateTimeOffset RecordFailure(DateTimeOffset now, string detail, string? note = null)
    {
        ConsecutiveFailures++;

        // 1, 2, 4, 8, 15, 15, ... minutes. The shift is bounded so a long outage cannot
        // overflow it into nonsense.
        var exponent = Math.Min(ConsecutiveFailures - 1, 10);
        var until = now + Clamp(MinimumPause * (1 << exponent), MinimumPause, MaximumFailurePause);

        PausedUntil = until;
        PausedNote = note;
        PausedReason = $"{detail}; next live attempt after {until.ToLocalTime():HH:mm} " +
                       $"(failure {ConsecutiveFailures} in a row)";
        return until;
    }

    /// <summary>
    /// Records a reading. Clears the schedule.
    /// </summary>
    /// <returns>How many failed attempts preceded this success — non-zero means "recovered".</returns>
    public int RecordSuccess()
    {
        var recoveredFrom = ConsecutiveFailures;
        ConsecutiveFailures = 0;
        PausedUntil = null;
        PausedNote = null;
        PausedReason = null;
        return recoveredFrom;
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) =>
        value < min ? min : value > max ? max : value;
}
