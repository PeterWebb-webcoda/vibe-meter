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
    IReadOnlyList<ClaudeModelCost> WeeklyModelCosts);
