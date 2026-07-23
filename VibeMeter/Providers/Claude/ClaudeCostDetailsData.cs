using System;
using System.Collections.Generic;

namespace VibeMeter.Providers.Claude;

public record ClaudeModelCost(
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens,
    decimal TotalCostUsd);

public record ClaudeCostDetailsData(
    decimal TodayTotalCostUsd,
    long TodayTotalTokens,
    decimal WeekTotalCostUsd,
    long WeekTotalTokens,
    decimal MonthTotalCostUsd,
    long MonthTotalTokens,
    decimal FiveHourCostUsd,
    long FiveHourTokens,
    IReadOnlyList<ClaudeModelCost> WeeklyModelCosts)
{
    /// <summary>Weekly cache-read tokens (served from cache, not reprocessed).</summary>
    public long WeekCacheReadTokens { get; init; }

    /// <summary>Weekly cache-write tokens (context written to cache for future reuse).</summary>
    public long WeekCacheWriteTokens { get; init; }

    /// <summary>Weekly input tokens that were NOT served from cache (reprocessed).</summary>
    public long WeekUncachedInputTokens { get; init; }

    /// <summary>
    /// Returns a copy of this record whose cost/token totals are no lower than
    /// <paramref name="floor"/>. Absorbs measurement dips caused by the background
    /// calculator racing with Claude Code appending to a live transcript: token spend
    /// is monotonic within a window, so any decrease is a read artifact.
    /// </summary>
    public ClaudeCostDetailsData WithMonotonicFloor(ClaudeCostDetailsData floor) => this with
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
};
