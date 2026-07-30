using System.Collections.Generic;

namespace VibeMeter.Providers.Claude;

public record ClaudeModelCost(
    string ModelId,
    long InputTokens,
    long OutputTokens,
    long CacheWriteTokens,
    long CacheReadTokens,
    decimal TotalCostUsd,
    bool IsEstimatedRate = false);

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

};
