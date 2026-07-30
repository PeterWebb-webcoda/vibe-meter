using System.Collections.Generic;

namespace VibeMeter.Providers.Codex;

/// <summary>One Codex model's weekly token usage and estimated cost.</summary>
/// <param name="IsEstimatedRate"><c>true</c> when this model's rate is unverified/estimated
/// — the UI flags it so a guess doesn't read as a known rate (Bug 4).</param>
public record CodexModelCost(
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    decimal TotalCostUsd,
    bool IsEstimatedRate = false);

/// <summary>
/// Aggregate Codex cost/token window totals plus a per-model weekly breakdown.
/// Mirrors <see cref="Claude.ClaudeCostDetailsData"/>. Costs are estimates computed from
/// a pricing table (no per-request $ in Codex transcripts) — see
/// <see cref="CodexCostCalculator"/>.
/// </summary>
public record CodexCostDetailsData(
    decimal TodayTotalCostUsd,
    long TodayTotalTokens,
    decimal WeekTotalCostUsd,
    long WeekTotalTokens,
    decimal MonthTotalCostUsd,
    long MonthTotalTokens,
    decimal FiveHourCostUsd,
    long FiveHourTokens,
    IReadOnlyList<CodexModelCost> WeeklyModelCosts)
{
    /// <summary>Weekly cached input tokens (served from cache at a discount).</summary>
    public long WeekCachedInputTokens { get; init; }

    /// <summary>Weekly input tokens that were NOT cached (billed at the full input rate).</summary>
    public long WeekUncachedInputTokens { get; init; }

    /// <summary>
    /// True when any weekly model's rate is an unverified estimate. Drives the window-level
    /// "estimated" marker so the whole panel reads as approximate (Bug 4).
    /// </summary>
    public bool HasEstimatedRates { get; init; }

}
