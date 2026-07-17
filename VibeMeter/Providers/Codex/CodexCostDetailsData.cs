using System.Collections.Generic;

namespace VibeMeter.Providers.Codex;

/// <summary>One Codex model's weekly token usage and estimated cost.</summary>
public record CodexModelCost(
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CachedInputTokens,
    long ReasoningTokens,
    decimal TotalCostUsd);

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
}
