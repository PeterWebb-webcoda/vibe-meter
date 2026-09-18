using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VibeMeter.Core;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// Anthropic Claude (Pro/Max subscription) provider. Normalises the 5-hour and 7-day
/// rolling windows into <see cref="ProviderUsage"/> gauges — the same figures Claude
/// Code's own <c>/usage</c> command displays.
/// </summary>
/// <remarks>
/// <para>
/// Three sources can supply those figures, and they are tried in that order of preference:
/// Anthropic's usage endpoint fetched live, the Claude Code CLI's <c>usage_cache.json</c>,
/// and the desktop app's sampled <c>plan-usage-history.json</c>.
/// </para>
/// <para>
/// The live source was added because neither file is dependable. <c>usage_cache.json</c>
/// is not maintained by Claude Code — it is written only on an explicit <c>/usage</c> or a
/// limit-approaching warning, and was measured sitting untouched for over a day across
/// 2h10m of continuous heavy use — while the desktop history samples at a 30-minute median
/// against a 20-minute staleness gate, so it is stale more often than not. Reading files
/// alone worked reliably on exactly one machine, and only because a personal statusline
/// script happened to fetch usage and write the cache itself.
/// </para>
/// <para>
/// Adding the live source takes nothing away: a machine with no credential, a lapsed one,
/// or no network falls back to precisely the selection it made before.
/// </para>
/// </remarks>
public sealed class ClaudeProvider : IUsageProvider
{
    public string Id => "claude";
    public string DisplayName => "Claude Code";

    /// <summary>What to run when the sign-in has lapsed or been refused.</summary>
    private const string SignInCommand = "run `/login` in Claude Code";

    private readonly ClaudeAuth _auth;
    private readonly Func<ClaudeApiClient> _clientFactory;
    private static Task<ClaudeCostDetailsData?>? _costTask;
    private static ClaudeCostDetailsData? _lastCostData;

    // Anthropic's data names these windows outright — the cache fields are
    // literally "five_hour" and "seven_day", and the limit kinds are "session" /
    // "weekly_all" / "weekly_scoped" — so these constants transcribe the window
    // lengths the source itself declares. Nothing is inferred from a gauge title,
    // id, or display string; a provider that only labels its windows is NOT
    // granted a window here on that basis.
    internal const int FiveHourWindowSeconds = 5 * 60 * 60;      // 18 000
    internal const int SevenDayWindowSeconds = 7 * 24 * 60 * 60; // 604 800

    /// <summary>Production constructor.</summary>
    public ClaudeProvider() : this(new ClaudeAuth()) { }

    /// <summary>Testable constructor.</summary>
    public ClaudeProvider(ClaudeAuth auth) : this(auth, () => new ClaudeApiClient()) { }

    /// <summary>Testable constructor, with the transport supplied.</summary>
    internal ClaudeProvider(ClaudeAuth auth, Func<ClaudeApiClient> clientFactory)
    {
        _auth = auth;
        _clientFactory = clientFactory;
    }

    public async Task<ProviderUsage> FetchAsync()
    {
        // 1. Not installed / no local state at all?
        if (!_auth.IsConfigured)
        {
            return NotConfigured(
                "Install and sign in to Claude on this PC " +
                "(the CLI or the desktop app), then refresh.");
        }

        // 2. Read account / plan metadata (non-secret). Absent for desktop-only users,
        //    which costs us the plan label but nothing else.
        ClaudeOAuthAccount? account;
        try
        {
            account = await _auth.GetAccountAsync();
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        // 3. Ask Anthropic directly. This is the only source whose freshness does not depend
        //    on another program having run recently, so it outranks both files.
        var live = await TryFetchLiveAsync(_auth, _clientFactory);

        // 4. Merge with the local surfaces. The live reading wins when there is one;
        //    otherwise the selection is exactly what it was before this source existed —
        //    the richest local surface that has any figures, NOT simply the most recently
        //    written one (see ClaudeUsageSources.Merge for why that distinction is the
        //    whole bug that selection was rewritten to fix).
        var snapshot = await ClaudeUsageSources.ReadBestAsync(live.Snapshot);
        if (snapshot is null)
        {
            // A lapsed sign-in with nothing on disk is a specific, fixable state, so say so
            // rather than falling back to the generic "nothing written yet" advice.
            return NotConfigured(live.Note ??
                "Claude is installed but hasn't written any usage figures yet. " +
                "Run Claude Code or open the Claude desktop app for a minute, then refresh. " +
                $"Looked in: {string.Join(" and ", ClaudeUsageSources.CandidatePaths)}");
        }

        // Trigger cost calculation in the background so we don't block normal UI load.
        // It reads many JSONL files and can take 1-2 seconds.
        if (_costTask == null || _costTask.IsCompleted)
        {
            if (_costTask?.IsCompletedSuccessfully == true && _costTask.Result is { } fresh)
            {
                // Every displayed window is time-bounded. Accept decreases so the Today
                // bucket can reset at local midnight, rolling windows can age out, and a
                // corrected rate table takes effect without requiring an app restart.
                _lastCostData = fresh;
            }
            _costTask = Task.Run(() => ClaudeCostCalculator.CalculateCostsAsync());
        }

        ClaudeCostDetailsData? costData = _lastCostData;

        // 5. Normalise into gauges.
        var gauges = BuildGauges(snapshot, costData, DisplayName);

        // The tier from .claude.json stays first: it is the most specific thing available,
        // distinguishing "Claude Max 5x" from "Claude Max 20x". The credential's own fields
        // are additions below it, not a replacement — the credential states its tier in the
        // same shape, and its subscriptionType says only "max". Together they give a plan
        // label to desktop-only and freshly-signed-in machines that had none before.
        string? planLabel =
            ClaudeAuth.FriendlyTier(account?.UserRateLimitTier)
            ?? ClaudeAuth.FriendlyTier(live.Credential?.RateLimitTier)
            ?? ClaudeAuth.FriendlySubscription(live.Credential?.SubscriptionType);
        string? resetNote = null;
        if (snapshot.SevenDayResetAt is { } weeklyReset)
        {
            var qualifier = snapshot.ResetTimesAreApproximate ? "~" : "";
            resetNote = $"Weekly reset: {qualifier}{weeklyReset:MMM d, h:mm tt}";
        }

        // 6. Staleness heads-up — the figures are only as fresh as the last Claude refresh,
        //    judged against the cadence of whichever surface supplied them. A sampled source
        //    is not at fault for being between samples, so the desktop history is allowed to
        //    miss several of its own 30-minute samples before we say anything; the CLI cache,
        //    which is rewritten on use and so has no cadence, keeps the flat six-hour rule.
        //    A live reading is never stale: it was observed at this poll.
        var now = DateTime.Now;
        string? errorMessage = null;
        var age = now - snapshot.ObservedAt;
        if (!snapshot.IsCurrentAt(now))
        {
            errorMessage = $"Figures are {Math.Floor(age.TotalHours)}h old — open Claude to refresh.";

            // Only here. A lapsed sign-in that costs the user nothing — because the files
            // are current — is not worth an error on a working card; a lapsed sign-in that
            // left them looking at day-old figures is exactly what they need told, because
            // renewing it would have prevented this.
            if (live.Note is not null) errorMessage += $" {live.Note}";
        }

        return new ProviderUsage
        {
            ProviderId = Id,
            DisplayName = DisplayName,
            State = ProviderState.Ok,
            PlanLabel = planLabel,
            ResetNote = resetNote,
            Gauges = gauges,
            ErrorMessage = errorMessage,
            ExtensionData = costData,

            // These figures come from local files that only refresh while a Claude
            // surface runs on THIS machine, so the snapshot's own observation time —
            // not this poll — is the honest age for the agent's freshness gate.
            SourceObservedAt = new DateTimeOffset(snapshot.ObservedAt),

            // Two very different surfaces can land here. Say which one won, so a log line
            // about a stale or omitted Claude reading names the file it is talking about.
            SourceLabel = snapshot.SourceLabel,
        };
    }

    /// <summary>
    /// What one attempt at the live source produced.
    /// </summary>
    /// <param name="Snapshot">The live reading, or null when there was not one.</param>
    /// <param name="Credential">
    /// The credential that was read, for its plan fields. Null when there is no sign-in on
    /// this machine.
    /// </param>
    /// <param name="Note">
    /// A sentence explaining why the live source is unavailable and what fixes it, or null.
    /// Deliberately null for a transient failure and for a machine that simply has no
    /// Claude Code sign-in: neither is the user's to act on, and neither is a fault.
    /// </param>
    internal readonly record struct LiveUsageAttempt(
        ClaudeUsageSnapshot? Snapshot,
        ClaudeCredential? Credential,
        string? Note);

    /// <summary>
    /// Reads the stored credential and, if it is usable, fetches usage live.
    /// </summary>
    /// <remarks>
    /// Internal and static so the tests can drive it with a stubbed transport, without
    /// depending on which Claude files the build agent happens to have.
    /// </remarks>
    internal static async Task<LiveUsageAttempt> TryFetchLiveAsync(
        ClaudeAuth auth,
        Func<ClaudeApiClient> clientFactory)
    {
        var credential = await auth.GetCredentialAsync();

        // No sign-in here. Perfectly ordinary — a desktop-only user never has one — so the
        // card says nothing and the local files carry the reading, exactly as before.
        if (credential is null || !credential.HasToken)
        {
            return new LiveUsageAttempt(null, credential, null);
        }

        // A lapsed token earns an opaque 401, which reads as "Claude is broken" rather than
        // "your sign-in lapsed". Do not spend a request finding that out.
        if (credential.IsExpiredAt(DateTimeOffset.UtcNow))
        {
            var expiry = credential.ExpiresAt!.Value.ToLocalTime();
            return new LiveUsageAttempt(null, credential,
                $"Claude sign-in expired {expiry:yyyy-MM-dd} — {SignInCommand} on this PC to renew it.");
        }

        using var client = clientFactory();
        var result = await client.GetUsageAsync(credential.AccessToken!);

        return result.Outcome switch
        {
            ClaudeApiOutcome.Success => new LiveUsageAttempt(
                // Observed now, because we asked now. That is the whole point of this source.
                ClaudeUsageSources.FromLiveApi(result.Data, DateTime.Now), credential, null),

            ClaudeApiOutcome.CredentialRejected => new LiveUsageAttempt(null, credential,
                $"Claude sign-in was rejected — {SignInCommand} on this PC to renew it."),

            // Transient: no network, a timeout, a 5xx. Nothing for the user to do, and the
            // local files are there precisely for this. Stay quiet.
            _ => new LiveUsageAttempt(null, credential, null),
        };
    }

    private ProviderUsage NotConfigured(string message) => new()
    {
        ProviderId = Id,
        DisplayName = DisplayName,
        State = ProviderState.NotConfigured,
        ErrorMessage = message
    };

    private ProviderUsage Error(string message) => new()
    {
        ProviderId = Id,
        DisplayName = DisplayName,
        State = ProviderState.Error,
        ErrorMessage = message
    };

    /// <summary>
    /// Normalises a usage snapshot into gauges. Internal for tests: the
    /// named-window mapping below is contract-relevant (the agent publishes
    /// <c>ResetWindowSeconds</c>), and this keeps it verifiable without real
    /// Claude files on disk.
    /// </summary>
    internal static List<UsageGauge> BuildGauges(
        ClaudeUsageSnapshot snapshot,
        ClaudeCostDetailsData? costData,
        string displayName)
    {
        var gauges = new List<UsageGauge>();
        var provenance = Provenance(snapshot);

        if (snapshot.FiveHourPercentUsed is { } fiveHourUsed)
        {
            gauges.Add(new UsageGauge(
                Id: "claude-5h",
                Title: "5h",
                Subtitle: displayName,
                PercentRemaining: RemainingFrom(fiveHourUsed),
                ResetAt: snapshot.FiveHourResetAt,
                ResetWindowSeconds: FiveHourWindowSeconds,
                TooltipText: Tooltip(
                    costData != null ? $"Cost in last 5h: ${costData.FiveHourCostUsd:F2} ({costData.FiveHourTokens:N0} billed tokens)" : null,
                    provenance)));
        }

        if (snapshot.SevenDayPercentUsed is { } sevenDayUsed)
        {
            gauges.Add(new UsageGauge(
                Id: "claude-weekly",
                Title: "Weekly",
                Subtitle: displayName,
                PercentRemaining: RemainingFrom(sevenDayUsed),
                ResetAt: snapshot.SevenDayResetAt,
                ResetWindowSeconds: SevenDayWindowSeconds,
                TooltipText: Tooltip(
                    costData != null ? $"Cost in last 7 days: ${costData.WeekTotalCostUsd:F2} ({costData.WeekTotalTokens:N0} billed tokens)" : null,
                    provenance)));
        }

        // Model-scoped weekly limits (e.g. a separate Fable 5 allowance) are only recorded
        // by the CLI cache; the desktop history has no equivalent, so this list is simply
        // empty for desktop-only users.
        foreach (var limit in snapshot.ScopedLimits)
        {
            var modelName = limit.Scope?.Model?.DisplayName;
            if (string.IsNullOrWhiteSpace(modelName)) continue;

            gauges.Add(new UsageGauge(
                Id: $"claude-weekly-{modelName.ToLowerInvariant()}",
                Title: $"Weekly ({modelName})",
                Subtitle: displayName,
                PercentRemaining: RemainingFrom(limit.Percent ?? 0),
                ResetAt: limit.ResetAt,

                // ClaudeUsageSources only files limits with Kind
                // "weekly_scoped" here, so the limit's own kind names its
                // window as weekly — the same declared meaning as seven_day.
                ResetWindowSeconds: SevenDayWindowSeconds,
                TooltipText: provenance));
        }

        return gauges;
    }

    /// <summary>Converts a "percent used" value into a clamped "percent remaining".</summary>
    private static int RemainingFrom(int usedPercent) =>
        Math.Max(0, Math.Min(100, 100 - usedPercent));

    /// <summary>
    /// Names the file the figures came from, and warns when its reset times were inferred
    /// rather than reported — so an estimated countdown is never mistaken for an exact one.
    /// </summary>
    private static string Provenance(ClaudeUsageSnapshot snapshot)
    {
        var line = $"Source: {snapshot.SourceLabel}, read {snapshot.ObservedAt:MMM d, h:mm tt}";
        return snapshot.ResetTimesAreApproximate
            ? line + " (reset times estimated from sampled history)"
            : line;
    }

    private static string Tooltip(string? detail, string provenance) =>
        string.IsNullOrEmpty(detail) ? provenance : $"{detail}\n{provenance}";
}
