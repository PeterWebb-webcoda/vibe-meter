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
/// Anthropic does not expose a documented public usage REST API for subscription plans;
/// instead each Claude surface persists utilisation locally. Which file exists depends on
/// how the user runs Claude, so path discovery is delegated to
/// <see cref="ClaudeUsageSources"/> rather than assuming the CLI cache. No OAuth token
/// handling or network call is required either way.
/// </remarks>
public sealed class ClaudeProvider : IUsageProvider
{
    public string Id => "claude";
    public string DisplayName => "Claude Code";

    private readonly ClaudeAuth _auth;
    private static Task<ClaudeCostDetailsData?>? _costTask;
    private static ClaudeCostDetailsData? _lastCostData;

    /// <summary>Production constructor.</summary>
    public ClaudeProvider() : this(new ClaudeAuth()) { }

    /// <summary>Testable constructor.</summary>
    public ClaudeProvider(ClaudeAuth auth) => _auth = auth;

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

        // 3. Read usage from whichever local surface has the freshest figures.
        var snapshot = await ClaudeUsageSources.ReadBestAsync();
        if (snapshot is null)
        {
            return NotConfigured(
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

        // 4. Normalise into gauges.
        var gauges = new List<UsageGauge>();
        var provenance = Provenance(snapshot);

        if (snapshot.FiveHourPercentUsed is { } fiveHourUsed)
        {
            gauges.Add(new UsageGauge(
                Id: "claude-5h",
                Title: "5h",
                Subtitle: DisplayName,
                PercentRemaining: RemainingFrom(fiveHourUsed),
                ResetAt: snapshot.FiveHourResetAt,
                TooltipText: Tooltip(
                    costData != null ? $"Cost in last 5h: ${costData.FiveHourCostUsd:F2} ({costData.FiveHourTokens:N0} billed tokens)" : null,
                    provenance)));
        }

        if (snapshot.SevenDayPercentUsed is { } sevenDayUsed)
        {
            gauges.Add(new UsageGauge(
                Id: "claude-weekly",
                Title: "Weekly",
                Subtitle: DisplayName,
                PercentRemaining: RemainingFrom(sevenDayUsed),
                ResetAt: snapshot.SevenDayResetAt,
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
                Subtitle: DisplayName,
                PercentRemaining: RemainingFrom(limit.Percent ?? 0),
                ResetAt: limit.ResetAt,
                TooltipText: provenance));
        }

        string? planLabel = ClaudeAuth.FriendlyTier(account?.UserRateLimitTier);
        string? resetNote = null;
        if (snapshot.SevenDayResetAt is { } weeklyReset)
        {
            var qualifier = snapshot.ResetTimesAreApproximate ? "~" : "";
            resetNote = $"Weekly reset: {qualifier}{weeklyReset:MMM d, h:mm tt}";
        }

        // 5. Staleness heads-up — the figures are only as fresh as the last Claude refresh.
        string? errorMessage = null;
        var age = DateTime.Now - snapshot.ObservedAt;
        if (age.TotalHours > 6)
        {
            errorMessage = $"Figures are {Math.Floor(age.TotalHours)}h old — open Claude to refresh.";
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
            ExtensionData = costData
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
