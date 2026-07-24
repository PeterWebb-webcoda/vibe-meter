using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// Aggregates Claude Code token usage and cost from local transcript files.
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>%USERPROFILE%\.claude\projects\**\*.jsonl</c> — the per-project conversation
/// transcripts the Claude Code CLI writes. Each <c>type:"assistant"</c> line carries a
/// <c>message.usage</c> block (input/output/cache tokens), a <c>message.model</c> (which
/// varies per record), and optionally a transcript-native <c>costUSD</c>.
/// </para>
/// <para>
/// <b>Performance:</b> to avoid re-reading the whole corpus every refresh, each file's
/// parsed records are cached keyed on its <c>LastWriteTimeUtc</c>. Only files whose mtime
/// changed are re-parsed; the rest are re-folded from cache.
/// </para>
/// </remarks>
public sealed class ClaudeCostCalculator
{
    private static readonly string ProjectsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "projects");

    /// <summary>
    /// Per-file cache keyed by full path. The value holds the file's mtime (to detect
    /// appends) and the parsed records. Static so it survives across calc runs.
    /// </summary>
    private static readonly Dictionary<string, FileCacheEntry> FileCache = new(StringComparer.Ordinal);

    public static async Task<ClaudeCostDetailsData?> CalculateCostsAsync()
    {
        if (!Directory.Exists(ProjectsDir))
            return null;

        // 1. Enumerate current files + mtimes; track live paths for eviction.
        var liveFiles = new List<(string Path, DateTime Mtime)>();
        foreach (var path in Directory.EnumerateFiles(ProjectsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(path); }
            catch { continue; }
            liveFiles.Add((path, mtime));
        }
        var livePaths = liveFiles.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        // 2. Refresh the cache: parse only new/changed files.
        foreach (var (path, mtime) in liveFiles)
        {
            if (FileCache.TryGetValue(path, out var cached) && cached.Mtime == mtime)
                continue;

            var entries = await ParseFileAsync(path);
            FileCache[path] = new FileCacheEntry(mtime, entries);
        }

        // 3. Evict cache entries for deleted files (only when it could matter).
        if (FileCache.Count > liveFiles.Count + 16)
        {
            var stale = FileCache.Keys.Where(p => !livePaths.Contains(p)).ToList();
            foreach (var p in stale) FileCache.Remove(p);
        }

        // 4. Re-fold all cached entries into fresh aggregates.
        var now = DateTime.UtcNow;
        var monthAgo = now.AddDays(-30);
        var weekAgo = now.AddDays(-7);
        var fiveHoursAgo = now.AddHours(-5);
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local).Date;

        decimal todayCost = 0, weekCost = 0, monthCost = 0, fiveHCost = 0;
        long todayTokens = 0, weekTokens = 0, monthTokens = 0, fiveHTokens = 0;
        long weekCacheRead = 0, weekCacheWrite = 0, weekUncachedInput = 0;
        var weeklyModelStats = new Dictionary<string, ModelStats>();

        foreach (var cacheEntry in FileCache.Values)
        {
            foreach (var r in cacheEntry.Records)
            {
                if (r.TimestampUtc < monthAgo) continue;

                long totalTokens = r.Input + r.Output + r.CacheWrite + r.CacheRead;

                // Monthly aggregation
                monthTokens += totalTokens;
                monthCost += r.Cost;

                if (r.TimestampUtc >= weekAgo)
                {
                    weekTokens += totalTokens;
                    weekCost += r.Cost;
                    weekCacheRead += r.CacheRead;
                    weekCacheWrite += r.CacheWrite;
                    weekUncachedInput += r.Input;

                    if (!weeklyModelStats.TryGetValue(r.Model, out var stats))
                    {
                        stats = new ModelStats();
                        weeklyModelStats[r.Model] = stats;
                    }
                    stats.Input += r.Input;
                    stats.Output += r.Output;
                    stats.CacheWrite += r.CacheWrite;
                    stats.CacheRead += r.CacheRead;
                    stats.Cost += r.Cost;
                }

                // Today (local-midnight boundary)
                if (TimeZoneInfo.ConvertTimeFromUtc(r.TimestampUtc, TimeZoneInfo.Local).Date == todayLocal)
                {
                    todayTokens += totalTokens;
                    todayCost += r.Cost;
                }

                // 5h
                if (r.TimestampUtc >= fiveHoursAgo)
                {
                    fiveHTokens += totalTokens;
                    fiveHCost += r.Cost;
                }
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

    /// <summary>
    /// Parses one transcript file into a list of per-record usage entries. The model and
    /// the resolved cost are captured per record (Claude's model varies per line, and cost
    /// prefers a native <c>costUSD</c> when present). Records older than 30 days are
    /// dropped — they can never re-enter any window.
    /// </summary>
    private static async Task<List<FileEntry>> ParseFileAsync(string path)
    {
        var records = new List<FileEntry>();
        var monthAgo = DateTime.UtcNow.AddDays(-30);

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "assistant" ||
                    !root.TryGetProperty("timestamp", out var tsEl))
                {
                    continue;
                }

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

                // Prefer the transcript-native costUSD; fall back to the pricing table.
                decimal cost = root.TryGetProperty("costUSD", out var costEl) && costEl.TryGetDecimal(out var parsedCost)
                    ? parsedCost
                    : CalculateCost(model, input, output, cacheWrite, cacheRead);

                records.Add(new FileEntry(utcTs, model, input, output, cacheWrite, cacheRead, cost));
            }
        }
        catch
        {
            // Unreadable / malformed file — return whatever we parsed so far.
        }

        return records;
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

    /// <summary>One per-record usage entry, cached. Model + cost are per-record for Claude.</summary>
    private sealed record FileEntry(
        DateTime TimestampUtc,
        string Model,
        long Input,
        long Output,
        long CacheWrite,
        long CacheRead,
        decimal Cost);

    /// <summary>Cache value: the file's mtime when parsed and its records.</summary>
    private sealed class FileCacheEntry
    {
        public FileCacheEntry(DateTime mtime, List<FileEntry> records)
        {
            Mtime = mtime;
            Records = records;
        }
        public DateTime Mtime { get; }
        public List<FileEntry> Records { get; }
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
