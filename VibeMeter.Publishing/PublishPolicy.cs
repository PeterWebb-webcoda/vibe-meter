namespace VibeMeter.Publishing;

/// <summary>
/// Why a cycle did or did not publish. Every value is either a publish reason
/// or a skip reason — <see cref="PublishDecision.ShouldPublish"/> says which,
/// so no call site has to reason about the enum to get the answer.
/// </summary>
public enum PublishReason
{
    /// <summary>Nothing has ever been published from this state, so there is no baseline to compare against.</summary>
    FirstPublish,

    /// <summary>The content fingerprint differs from the last published one, and the minimum interval has elapsed.</summary>
    ContentChanged,

    /// <summary>Nothing changed, but the heartbeat fell due — see <see cref="PublishPolicyOptions.HeartbeatInterval"/>.</summary>
    Heartbeat,

    /// <summary>
    /// Nothing changed and the heartbeat is not due, but this host has started
    /// (or been rebuilt) since its last row and has not spent its one startup
    /// publish yet — see <c>publishOnStart</c> on
    /// <see cref="PublishPolicy(IPublishPolicyStore, PublishPolicyOptions?, bool)"/>.
    /// </summary>
    HostRestarted,

    /// <summary>
    /// The recorded publish time is in the future: the clock moved backwards,
    /// or the state came from a machine whose clock was ahead. Publishing is
    /// the safe answer — see <see cref="PublishPolicy.Decide"/>.
    /// </summary>
    ClockWentBackwards,

    /// <summary>Skipped: the minimum interval has not elapsed, whether or not the content changed.</summary>
    TooSoon,

    /// <summary>Skipped: the interval has elapsed but nothing changed and the heartbeat is not yet due.</summary>
    Unchanged,
}

/// <summary>
/// What <see cref="PublishPolicy.Decide"/> concluded, with a line fit for a log
/// so a host can say WHY it stayed quiet rather than leaving an operator to
/// guess whether publishing is broken.
/// </summary>
public sealed record PublishDecision(bool ShouldPublish, PublishReason Reason, string Explanation);

/// <summary>
/// Everything the policy remembers between cycles: the fingerprint of the last
/// document handed to the API and when that happened. Both null before anything
/// has been published.
/// </summary>
/// <remarks>
/// Small and boring on purpose — it has to be cheap to persist, safe to lose
/// (losing it costs one extra publish, never data) and meaningless to anyone
/// but this policy.
/// </remarks>
public sealed record PublishPolicyState(string? Fingerprint, DateTimeOffset? LastPublishedAt)
{
    /// <summary>Nothing published yet: the state a fresh host, or an unreadable store, starts from.</summary>
    public static readonly PublishPolicyState None = new(null, null);
}

/// <summary>
/// The two intervals that bound how often a host writes a row, with defaults
/// argued from this application's actual cadences rather than chosen for being
/// round.
/// </summary>
public sealed record PublishPolicyOptions(TimeSpan MinimumInterval, TimeSpan HeartbeatInterval)
{
    /// <summary>
    /// Four minutes fifty seconds.
    /// <para>
    /// The tray app refreshes every 60 seconds by default
    /// (<c>SettingsData.RefreshIntervalSeconds</c>), so publishing on every
    /// refresh is about 1,440 near-identical rows per user per day against a
    /// DTU-constrained shared database. A floor of 290 s makes that at most one
    /// publish per five refreshes — about 298 rows a day in the WORST case,
    /// where something genuinely changes every single cycle.
    /// </para>
    /// <para>
    /// 290 rather than 300 because the host's refresh timer and this policy's
    /// clock are not the same edge: the decision is taken partway through a
    /// refresh, and a timer that fires "every 60 seconds" is only ever
    /// approximately that. At exactly 300 s the fifth refresh would sometimes
    /// fall a hair short, and every near miss costs a WHOLE refresh period —
    /// the cadence would oscillate between five and six minutes for no benefit.
    /// Undercutting by 10 s (a sixth of a refresh period) is far more than any
    /// plausible timer error and far less than one refresh, so the fifth
    /// refresh always clears the bar.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromSeconds(290);

    /// <summary>
    /// Twenty-three minutes.
    /// <para>
    /// Deliberately just ABOVE the agent's 20-minute default staleness
    /// threshold (<c>AgentConfig.DefaultStalenessThreshold</c>): by the time a
    /// heartbeat falls due on an idle machine, a file-derived provider that has
    /// gone quiet has already aged out of the snapshot, so the heartbeat says
    /// "this machine is alive and that provider has nothing current" instead of
    /// restating figures the freshness gate is about to drop anyway. A shorter
    /// heartbeat would buy extra rows that carry no information.
    /// </para>
    /// <para>
    /// Close above it (three minutes, not three hours) so a reader can apply one
    /// simple rule — nothing in the last half hour means that machine is not
    /// reporting — and still get a missed heartbeat's worth of slack before
    /// believing it. And 23 is co-prime with both 60 and the agent's 5-minute
    /// interval, so machines that all start at the top of the hour drift apart
    /// instead of converging on the same minute and hitting the shared database
    /// together.
    /// </para>
    /// <para>
    /// The floor this sets is about 1,440 / 23 ≈ 62 rows per machine per day
    /// when absolutely nothing changes, against the 1,440 of publishing every
    /// refresh.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromMinutes(23);

    public static readonly PublishPolicyOptions Default =
        new(DefaultMinimumInterval, DefaultHeartbeatInterval);

    /// <summary>
    /// Both intervals must be positive, and the heartbeat must be longer than
    /// the minimum: a heartbeat at or below the floor would fall due on the
    /// first cycle the floor allows, which is not a heartbeat at all — it is
    /// publishing on every permitted cycle with the change check quietly
    /// disabled. Rejecting that here beats discovering it as a write rate in
    /// production.
    /// </summary>
    public PublishPolicyOptions Validated()
    {
        if (MinimumInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MinimumInterval), MinimumInterval, "The minimum publish interval must be positive.");
        }

        if (HeartbeatInterval <= MinimumInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatInterval),
                HeartbeatInterval,
                $"The heartbeat interval must be longer than the minimum publish interval ({MinimumInterval}).");
        }

        return this;
    }
}

/// <summary>
/// Decides whether a cycle's snapshot is worth a row.
/// </summary>
/// <remarks>
/// <para>
/// The decision itself is <see cref="Decide"/>: a static, pure function of
/// (previous fingerprint, last publish time, current fingerprint, now). It
/// reads no clock, touches no disk and publishes nothing, so every rule below
/// can be pinned by a test that simply passes the four values. The instance
/// members are the thin, impure rim around it — they fingerprint a mapped
/// snapshot and move the state to and from an
/// <see cref="IPublishPolicyStore"/>.
/// </para>
/// <para>
/// <b>Why the heartbeat is what makes the freshness timestamp honest.</b> The
/// collection API merges per provider by taking the first occurrence in
/// newest-first <c>observedAt</c> order, and <c>observedAt</c> is PUBLISH time,
/// not the data's own time. So the only thing a reader can read off a stored
/// row is "this machine last said so at T". Suppress unchanged publishes with
/// no heartbeat and T decays into "the last time these numbers MOVED" — after
/// which a machine that has been switched off for a week and a machine whose
/// quota simply has not budged for a week look identical, and the reader cannot
/// tell whether it is looking at a current figure or an abandoned one. The
/// heartbeat restores the original meaning: a host that re-states an unchanged
/// reading at a bounded interval is making a claim about the present, so a gap
/// longer than that interval genuinely means "that machine is not reporting"
/// rather than "nothing happened". The timestamp is only as honest as the
/// promise to keep repeating it.
/// </para>
/// </remarks>
public sealed class PublishPolicy
{
    private readonly IPublishPolicyStore _store;

    /// <summary>
    /// The unspent startup publish. Volatile because a host may consult the
    /// policy from a different thread-pool thread each cycle; the cycles
    /// themselves never overlap (the desktop host declines a refresh that
    /// arrives mid-publish, and the agent awaits each cycle), so a flag is
    /// enough and no lock is warranted.
    /// </summary>
    private volatile bool _restartPending;

    /// <param name="publishOnStart">
    /// Whether this host may spend ONE publish on having started, for figures
    /// that are otherwise unchanged and not yet due a heartbeat.
    /// <para>
    /// True for the desktop host: it is started and rebuilt by a person, and a
    /// relaunch that said nothing for up to a heartbeat (23 minutes) would look
    /// broken to whoever just switched publishing on. The allowance is spent
    /// once, by <see cref="RecordPublished"/>, and it is granted BELOW the
    /// minimum interval, not above it — a restart inside the floor waits for
    /// the floor rather than buying a row, which is what stops a run of
    /// restarts (or of saved settings) from writing a row apiece.
    /// </para>
    /// <para>
    /// False for the headless agent, which is restarted by service managers and
    /// supervisors: a crash loop must not turn into a write loop, and the agent
    /// keeps a persistent baseline precisely so a restart is invisible.
    /// </para>
    /// </param>
    public PublishPolicy(
        IPublishPolicyStore store,
        PublishPolicyOptions? options = null,
        bool publishOnStart = false)
    {
        _store = store;
        Options = (options ?? PublishPolicyOptions.Default).Validated();
        _restartPending = publishOnStart;
    }

    public PublishPolicyOptions Options { get; }

    /// <summary>
    /// The whole policy, as a pure function. Rule order matters and is the
    /// point of the design:
    /// <list type="number">
    /// <item>nothing published yet — publish, there is no baseline to compare;</item>
    /// <item>the recorded time is in the future — publish (see below);</item>
    /// <item>inside the minimum interval — skip, EVEN IF the content changed:
    /// this is the rule that caps the write rate, and a change-triggered
    /// exception to it would hand the cap straight back to a provider whose
    /// percentage ticks every cycle;</item>
    /// <item>the heartbeat is due — publish whatever the content says;</item>
    /// <item>the content changed — publish;</item>
    /// <item>this host has started since its last row and has not spent its
    /// startup publish — publish (see <paramref name="restartPending"/>);</item>
    /// <item>otherwise — skip.</item>
    /// </list>
    /// Nothing is lost by skipping a change inside the floor: the next cycle
    /// re-reads the providers, so the NEWER figures are published a moment
    /// later. The policy delays a reading; it never drops one.
    /// <para>
    /// The startup publish sits BELOW the minimum interval on purpose. A host
    /// that has just started has a real claim to make — it may have been off for
    /// a week — but it has no claim to make it NOW, and a host that is rebuilt
    /// every time its settings are saved would otherwise write a row per save.
    /// Below the floor, the allowance survives until the floor lets it through,
    /// so a restart is never silently swallowed and never costs more than one
    /// row per minimum interval.
    /// </para>
    /// <para>
    /// A recorded time in the future can only mean the clock moved backwards
    /// (a correction, a VM resume, a state file written by a machine running
    /// ahead). Both intervals would then be unreachable until real time caught
    /// up, which could silence a host for hours — exactly the failure the
    /// heartbeat exists to prevent — so the policy publishes once, which
    /// rewrites the recorded time to something sane and costs a single row.
    /// </para>
    /// </summary>
    /// <param name="restartPending">
    /// Whether this host has started since its last recorded row and still has
    /// its one startup publish to spend. Ignored inside the minimum interval.
    /// </param>
    public static PublishDecision Decide(
        PublishPolicyState previous,
        string currentFingerprint,
        DateTimeOffset now,
        PublishPolicyOptions options,
        bool restartPending = false)
    {
        if (previous.LastPublishedAt is not { } lastPublishedAt || previous.Fingerprint is null)
        {
            return new PublishDecision(
                true,
                PublishReason.FirstPublish,
                "Publishing: nothing has been published from this host yet.");
        }

        var elapsed = now - lastPublishedAt;
        if (elapsed < TimeSpan.Zero)
        {
            return new PublishDecision(
                true,
                PublishReason.ClockWentBackwards,
                $"Publishing: the last recorded publish time is {Describe(-elapsed)} in the future " +
                "(the clock moved backwards), so the interval and heartbeat cannot be judged.");
        }

        var changed = !string.Equals(previous.Fingerprint, currentFingerprint, StringComparison.Ordinal);

        if (elapsed < options.MinimumInterval)
        {
            return new PublishDecision(
                false,
                PublishReason.TooSoon,
                $"Not publishing: only {Describe(elapsed)} since the last publish, under the " +
                $"{Describe(options.MinimumInterval)} minimum" +
                (changed
                    ? " - the changed figures go out on the first cycle after the minimum elapses."
                    : restartPending
                        ? " - this host has restarted, and its one startup publish goes out on the first cycle after the minimum elapses."
                        : " and nothing has changed."));
        }

        if (elapsed >= options.HeartbeatInterval)
        {
            return new PublishDecision(
                true,
                changed ? PublishReason.ContentChanged : PublishReason.Heartbeat,
                changed
                    ? $"Publishing: the figures changed and {Describe(elapsed)} has passed."
                    : $"Publishing a heartbeat: nothing has changed for {Describe(elapsed)}, past the " +
                      $"{Describe(options.HeartbeatInterval)} heartbeat, so the published timestamp keeps " +
                      "meaning \"this machine is still reporting\" rather than \"this machine may be off\".");
        }

        if (changed)
        {
            return new PublishDecision(
                true,
                PublishReason.ContentChanged,
                $"Publishing: the figures changed and {Describe(elapsed)} has passed since the last publish.");
        }

        if (restartPending)
        {
            return new PublishDecision(
                true,
                PublishReason.HostRestarted,
                $"Publishing: this host has started since its last row {Describe(elapsed)} ago. Nothing has " +
                "changed, but a relaunch is worth saying so once rather than staying silent until the " +
                $"{Describe(options.HeartbeatInterval)} heartbeat.");
        }

        return new PublishDecision(
            false,
            PublishReason.Unchanged,
            $"Not publishing: identical figures {Describe(elapsed)} on, and the " +
            $"{Describe(options.HeartbeatInterval)} heartbeat is not due.");
    }

    /// <summary>
    /// Reads the stored state and applies <see cref="Decide"/> to this cycle's
    /// mapped snapshot. Returns the decision AND the fingerprint it was made
    /// against, so a caller that goes on to publish records exactly what it
    /// compared rather than fingerprinting the document a second time.
    /// </summary>
    public (PublishDecision Decision, string Fingerprint) Evaluate(MappedSnapshot snapshot, DateTimeOffset now)
    {
        var fingerprint = ContentFingerprint.For(snapshot);
        return (Decide(_store.Read(), fingerprint, now, Options, _restartPending), fingerprint);
    }

    /// <summary>
    /// Records that this fingerprint was handed to the API at
    /// <paramref name="publishedAt"/>. Called for a snapshot that was QUEUED as
    /// well as one that was accepted: a queued document is delivered by a later
    /// flush, so treating it as published is what stops the next cycle
    /// re-sending the same figures under a new timestamp and putting two rows in
    /// for one reading. The heartbeat bounds the cost if that document is
    /// eventually discarded — at worst one heartbeat of silence, not permanent.
    /// <para>
    /// This is also where the startup publish is spent, whichever rule actually
    /// let the row through: a host that has said something since it started has
    /// nothing left to say about having started.
    /// </para>
    /// </summary>
    public void RecordPublished(string fingerprint, DateTimeOffset publishedAt)
    {
        _restartPending = false;
        _store.Write(new PublishPolicyState(fingerprint, publishedAt));
    }

    private static string Describe(TimeSpan span) =>
        span.TotalHours >= 1 ? $"{span.TotalHours:0.#}h"
        : span.TotalMinutes >= 1 ? $"{span.TotalMinutes:0.#} min"
        : $"{span.TotalSeconds:0.#} s";
}
