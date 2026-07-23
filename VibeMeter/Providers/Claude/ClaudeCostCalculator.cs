using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

public sealed class ClaudeCostCalculator
{
    private static readonly string ProjectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    public static async Task<ClaudeCostDetailsData?> CalculateCostsAsync()
    {
        if (!Directory.Exists(ProjectsDir))
            return null;

        var now = DateTime.UtcNow;
        var monthAgo = now.AddDays(-30);
        var weekAgo = now.AddDays(-7);
        var fiveHoursAgo = now.AddHours(-5);
        // "Today" rolls over at local midnight, not UTC midnight (otherwise AEST users
        // see the counter reset at 10am). Compare timestamps converted to local.
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local).Date;

        decimal todayCost = 0, weekCost = 0, monthCost = 0, fiveHCost = 0;
        long todayTokens = 0, weekTokens = 0, monthTokens = 0, fiveHTokens = 0;
        long weekCacheRead = 0, weekCacheWrite = 0, weekUncachedInput = 0;

        var weeklyModelStats = new Dictionary<string, ModelStats>();

        foreach (var file in Directory.EnumerateFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("type", out var typeEl) && typeEl.GetString() == "assistant" &&
                        root.TryGetProperty("timestamp", out var tsEl))
                    {
                        DateTime timestamp;
                        if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var unixMs))
                        {
                            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
                        }
                        else if (!tsEl.TryGetDateTime(out timestamp))
                        {
                            continue;
                        }

                        var utcTs = timestamp.ToUniversalTime();
                        if (utcTs < monthAgo) continue;

                        var msgEl = root.TryGetProperty("message", out var m) ? m : root;

                        string model = msgEl.TryGetProperty("model", out var modelEl) ? modelEl.GetString() ?? "unknown" : "unknown";
                        
                        long input = 0, output = 0, cacheWrite = 0, cacheRead = 0;
                        if (msgEl.TryGetProperty("usage", out var usageEl))
                        {
                            input = usageEl.TryGetProperty("input_tokens", out var it) ? it.GetInt32() : 0;
                            output = usageEl.TryGetProperty("output_tokens", out var ot) ? ot.GetInt32() : 0;
                            cacheWrite = usageEl.TryGetProperty("cache_creation_input_tokens", out var cct) ? cct.GetInt32() : 0;
                            cacheRead = usageEl.TryGetProperty("cache_read_input_tokens", out var crt) ? crt.GetInt32() : 0;
                        }

                        long totalTokens = input + output + cacheWrite + cacheRead;
                        decimal cost = 0;

                        if (root.TryGetProperty("costUSD", out var costEl) && costEl.TryGetDecimal(out var parsedCost))
                        {
                            cost = parsedCost;
                        }
                        else
                        {
                            cost = CalculateCost(model, input, output, cacheWrite, cacheRead);
                        }

                        // Monthly aggregation
                        monthTokens += totalTokens;
                        monthCost += cost;

                        if (utcTs >= weekAgo)
                        {
                            weekTokens += totalTokens;
                            weekCost += cost;
                            weekCacheRead += cacheRead;
                            weekCacheWrite += cacheWrite;
                            weekUncachedInput += input;

                            if (!weeklyModelStats.TryGetValue(model, out var stats))
                            {
                                stats = new ModelStats();
                                weeklyModelStats[model] = stats;
                            }
                            stats.Input += input;
                            stats.Output += output;
                            stats.CacheWrite += cacheWrite;
                            stats.CacheRead += cacheRead;
                            stats.Cost += cost;
                        }

                        // Today aggregation (local-midnight boundary)
                        if (TimeZoneInfo.ConvertTimeFromUtc(utcTs, TimeZoneInfo.Local).Date == todayLocal)
                        {
                            todayTokens += totalTokens;
                            todayCost += cost;
                        }

                        // 5h aggregation
                        if (utcTs >= fiveHoursAgo)
                        {
                            fiveHTokens += totalTokens;
                            fiveHCost += cost;
                        }
                    }
                }
            }
            catch
            {
                // Ignore unreadable files or malformed JSON
            }
        }

        var modelCosts = weeklyModelStats.Select(kvp => new ClaudeModelCost(
            kvp.Key,
            kvp.Value.Input,
            kvp.Value.Output,
            kvp.Value.CacheWrite,
            kvp.Value.CacheRead,
            kvp.Value.Cost
        )).OrderByDescending(m => m.TotalCostUsd).ToList();

        return new ClaudeCostDetailsData(
            todayCost, todayTokens,
            weekCost, weekTokens,
            monthCost, monthTokens,
            fiveHCost, fiveHTokens,
            modelCosts)
        {
            WeekCacheReadTokens = weekCacheRead,
            WeekCacheWriteTokens = weekCacheWrite,
            WeekUncachedInputTokens = weekUncachedInput,
        };
    }

    private static decimal CalculateCost(string model, long input, long output, long cacheWrite, long cacheRead)
    {
        // Simple fallback pricing based on LiteLLM rates (per 1M tokens)
        decimal inPrice = 3.00m;
        decimal outPrice = 15.00m;
        decimal cwPrice = 3.75m;
        decimal crPrice = 0.30m;

        if (model.Contains("opus", StringComparison.OrdinalIgnoreCase))
        {
            inPrice = 15.00m; outPrice = 75.00m; cwPrice = 18.75m; crPrice = 1.50m;
        }
        else if (model.Contains("haiku", StringComparison.OrdinalIgnoreCase))
        {
            inPrice = 0.80m; outPrice = 4.00m; cwPrice = 1.00m; crPrice = 0.08m;
        }
        else if (model.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
        {
            inPrice = 3.00m; outPrice = 15.00m; cwPrice = 3.75m; crPrice = 0.30m;
        }
        else if (model.Contains("fable", StringComparison.OrdinalIgnoreCase))
        {
            // Fable pricing is currently roughly the same as sonnet in general, assuming defaults here.
            inPrice = 3.00m; outPrice = 15.00m; cwPrice = 3.75m; crPrice = 0.30m;
        }

        decimal inputCost = ((input - cacheRead) / 1_000_000m) * inPrice + (cacheRead / 1_000_000m) * crPrice;
        if (inputCost < 0) inputCost = (input / 1_000_000m) * inPrice; // fallback if cacheRead > input logic is weird

        decimal outputCost = (output / 1_000_000m) * outPrice;
        decimal cacheWriteCost = (cacheWrite / 1_000_000m) * cwPrice;

        return inputCost + outputCost + cacheWriteCost;
    }

    private class ModelStats
    {
        public long Input;
        public long Output;
        public long CacheWrite;
        public long CacheRead;
        public decimal Cost;
    }
}
