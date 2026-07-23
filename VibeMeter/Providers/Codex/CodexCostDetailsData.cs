using System;
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

    /// <summary>
    /// Returns a copy of this record whose cost/token totals are no lower than
    /// <paramref name="floor"/>. Used to absorb measurement dips caused by the
    /// background calculator racing with Codex appending to a live session file:
    /// token spend is monotonic within a window, so any decrease is a read artifact.
    /// </summary>
    public CodexCostDetailsData WithMonotonicFloor(CodexCostDetailsData floor) => this with
    {
        TodayTotalCostUsd  = Math.Max(TodayTotalCostUsd,  floor.TodayTotalCostUsd),
        TodayTotalTokens   = Math.Max(TodayTotalTokens,   floor.TodayTotalTokens),
        WeekTotalCostUsd   = Math.Max(WeekTotalCostUsd,   floor.WeekTotalCostUsd),
        WeekTotalTokens    = Math.Max(WeekTotalTokens,    floor.WeekTotalTokens),
        MonthTotalCostUsd  = Math.Max(MonthTotalCostUsd,  floor.MonthTotalCostUsd),
        MonthTotalTokens   = Math.Max(MonthTotalTokens,   floor.MonthTotalTokens),
        FiveHourCostUsd    = FiveHourCostUsd,   // 5h genuinely rolls over — no floor
        FiveHourTokens     = FiveHourTokens,    // 5h genuinely rolls over — no floor
    };
}
