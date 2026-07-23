using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using VibeMeter.Core;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// Anthropic Claude Code (Pro/Max subscription) provider. Reads the usage cache that
/// the Claude Code CLI maintains locally (<c>%USERPROFILE%\.claude\usage_cache.json</c>)
/// and normalises the 5-hour and 7-day rolling windows into <see cref="ProviderUsage"/>
/// gauges — the same figures Claude Code's own <c>/usage</c> command displays.
/// </summary>
/// <remarks>
/// Claude Code does not expose a documented public usage REST API for subscription
/// plans; instead it persists this cache locally after each refresh. VibeMeter reads
/// that cache directly, so no OAuth token handling or network call is required.
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
        // 1. Not installed / not signed in?
        if (!_auth.IsConfigured)
        {
            return NotConfigured(
                "Install and sign in to Claude Code on this PC " +
                $"(creates {ClaudeAuth.SettingsFilePath}), then refresh.");
        }

        // 2. Read account / plan metadata (non-secret).
        ClaudeOAuthAccount? account;
        try
        {
            account = await _auth.GetAccountAsync();
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        // 3. Read the usage cache. Signed in but no cache yet -> still "not ready".
        if (!File.Exists(ClaudeAuth.CacheFilePath))
        {
            return NotConfigured(
                "Claude Code is signed in but has no usage cache yet. " +
                "Open Claude Code (the cache refreshes automatically), then refresh.");
        }

        ClaudeUsageCacheFile cache;
        try
        {
            await using var stream = File.OpenRead(ClaudeAuth.CacheFilePath);
            cache = await JsonSerializer.DeserializeAsync<ClaudeUsageCacheFile>(stream)
                ?? throw new InvalidOperationException("Failed to deserialize Claude usage cache.");
        }
        catch (Exception ex)
        {
            return Error($"Could not read {ClaudeAuth.CacheFilePath}: {ex.Message}");
        }

        // Trigger cost calculation in the background so we don't block normal UI load.
        // It reads many JSONL files and can take 1-2 seconds.
        if (_costTask == null || _costTask.IsCompleted)
        {
            if (_costTask?.IsCompletedSuccessfully == true && _costTask.Result is { } fresh)
            {
                // High-water-mark guard: token spend is monotonic within a window, so a
                // fresh result lower than the cached one is a measurement artifact (the
                // calc raced with Claude Code appending to a live transcript). Keep the
                // higher totals rather than letting the UI tick backwards.
                _lastCostData = _lastCostData is { } prev
                    ? fresh.WithMonotonicFloor(prev)
                    : fresh;
            }
            _costTask = Task.Run(() => ClaudeCostCalculator.CalculateCostsAsync());
        }

        ClaudeCostDetailsData? costData = _lastCostData;

        // 4. Normalise into gauges.
        var data = cache.Data;
        var gauges = new List<UsageGauge>();

        if (data?.FiveHour is { } fiveHour)
        {
            gauges.Add(new UsageGauge(
                Id: "claude-5h",
                Title: "5h",
                Subtitle: DisplayName,
                PercentRemaining: RemainingFrom(fiveHour.UsedPercent),
                ResetAt: fiveHour.ResetAt,
                TooltipText: costData != null ? $"Cost in last 5h: ${costData.FiveHourCostUsd:F2} ({costData.FiveHourTokens:N0} tokens)" : null));
        }

        if (data?.SevenDay is { } sevenDay)
        {
            gauges.Add(new UsageGauge(
                Id: "claude-weekly",
                Title: "Weekly",
                Subtitle: DisplayName,
                PercentRemaining: RemainingFrom(sevenDay.UsedPercent),
                ResetAt: sevenDay.ResetAt,
                TooltipText: costData != null ? $"Cost in last 7 days: ${costData.WeekTotalCostUsd:F2} ({costData.WeekTotalTokens:N0} tokens)" : null));
        }

        // Model-scoped weekly limits (e.g. a separate Fable 5 allowance) show up as
        // "weekly_scoped" entries in the structured limits array, each carrying the
        // model's display name. Surface one gauge per scoped model found.
        if (data?.Limits is { } limits)
        {
            foreach (var limit in limits)
            {
                if (limit.Kind != "weekly_scoped") continue;
                var modelName = limit.Scope?.Model?.DisplayName;
                if (string.IsNullOrWhiteSpace(modelName)) continue;

                gauges.Add(new UsageGauge(
                    Id: $"claude-weekly-{modelName.ToLowerInvariant()}",
                    Title: $"Weekly ({modelName})",
                    Subtitle: DisplayName,
                    PercentRemaining: RemainingFrom(limit.Percent ?? 0),
                    ResetAt: limit.ResetAt));
            }
        }

        string? planLabel = ClaudeAuth.FriendlyTier(account?.UserRateLimitTier);
        string? resetNote = null;
        if (data?.SevenDay?.ResetAt is { } weeklyReset)
        {
            resetNote = $"Weekly reset: {weeklyReset:MMM d, h:mm tt}";
        }

        // 5. Staleness heads-up — the cache is only as fresh as the last Claude Code refresh.
        string? errorMessage = null;
        var refreshedAt = ClaudeJson.ParseIso(cache.Timestamp);
        if (refreshedAt is { } ts)
        {
            var age = DateTime.Now - ts;
            if (age.TotalHours > 6)
            {
                errorMessage = $"Cache is {Math.Floor(age.TotalHours)}h old — open Claude Code to refresh.";
            }
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
}
