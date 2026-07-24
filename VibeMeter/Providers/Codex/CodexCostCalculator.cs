using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Codex;

/// <summary>
/// Aggregates Codex token usage and estimated cost from local session transcripts.
/// </summary>
/// <remarks>
/// <para>
/// Reads <c>%USERPROFILE%\.codex\sessions\**\*.jsonl</c> — the per-session rollout files
/// the Codex CLI/Desktop writes. Each file carries:
/// </para>
/// <list type="bullet">
/// <item><b>Model</b> on <c>turn_context</c> records at <c>payload.model</c> — one model
/// per session (stable within a file).</item>
/// <item><b>Tokens</b> on <c>event_msg/token_count</c> records at
/// <c>payload.info.last_token_usage</c> — these are per-response deltas (sum them); the
/// sibling <c>total_token_usage</c> is cumulative and must NOT be summed.</item>
/// </list>
/// <para>
/// <b>Performance:</b> the corpus can be large (1GB+ with multi-hundred-MB files whose
/// individual lines are tens of KB). To avoid re-reading the whole corpus every refresh,
/// each file's parsed records are cached keyed on its <c>LastWriteTimeUtc</c>. Only files
/// whose mtime changed (i.e. a live session is appending) are re-parsed; the rest are
/// re-folded from cache. Lines are also substring-prefiltered before JSON parsing so the
/// giant <c>session_meta</c>/message lines are skipped cheaply.
/// </para>
/// <para><b>Costs are estimates.</b> Codex transcripts carry no per-request $ figure
/// (unlike Claude's <c>costUSD</c>), so costs are computed from a best-known pricing
/// table — see <see cref="CalculateCost"/>. Reasoning output is billed at the output rate.</para>
/// </remarks>
public sealed class CodexCostCalculator
{
    private static readonly string SessionsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".codex", "sessions");

    /// <summary>
    /// Per-file cache keyed by full path. The value holds the file's mtime (so we can
    /// detect appends) and the parsed records. Static so it survives across calc runs,
    /// matching the provider's static <c>_lastCostData</c> lifecycle.
    /// </summary>
    private static readonly Dictionary<string, FileCacheEntry> FileCache = new(StringComparer.Ordinal);

    public static async Task<CodexCostDetailsData?> CalculateCostsAsync()
    {
        if (!Directory.Exists(SessionsDir))
            return null;

        // 1. Enumerate current files + their mtimes. Build the set of paths still on disk
        //    so we can evict cache entries for deleted sessions afterwards.
        var liveFiles = new List<(string Path, DateTime Mtime)>();
        foreach (var path in Directory.EnumerateFiles(SessionsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(path); }
            catch { continue; }
            liveFiles.Add((path, mtime));
        }

        var livePaths = liveFiles.Select(f => f.Path).ToHashSet(StringComparer.Ordinal);

        // 2. Refresh the cache: parse only files that are new or whose mtime changed.
        foreach (var (path, mtime) in liveFiles)
        {
            if (FileCache.TryGetValue(path, out var cached) && cached.Mtime == mtime)
                continue; // unchanged — reuse cached entries

            var (model, entries) = await ParseFileAsync(path);
            FileCache[path] = new FileCacheEntry(mtime, model, entries);
        }

        // 3. Evict cache entries for files no longer on disk (deleted/rolled sessions).
        if (FileCache.Count > liveFiles.Count + 16) // only sweep when it could matter
        {
            var stale = FileCache.Keys.Where(p => !livePaths.Contains(p)).ToList();
            foreach (var p in stale) FileCache.Remove(p);
        }

        // 4. Re-fold all cached entries into fresh aggregates. The fold is pure and the
        //    time-window predicates are re-evaluated, so records age out of windows
        //    naturally as wall-clock advances. For a 500k-record corpus this is sub-second.
        var now = DateTime.UtcNow;
        var monthAgo = now.AddDays(-30);
        var weekAgo = now.AddDays(-7);
        var fiveHoursAgo = now.AddHours(-5);
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local).Date;

        decimal todayCost = 0, weekCost = 0, monthCost = 0, fiveHCost = 0;
        long todayTokens = 0, weekTokens = 0, monthTokens = 0, fiveHTokens = 0;
        long weekCachedInput = 0, weekUncachedInput = 0;
        var weeklyModelStats = new Dictionary<string, ModelStats>();

        foreach (var entry in FileCache.Values)
        {
            foreach (var r in entry.Records)
            {
                if (r.TimestampUtc < monthAgo) continue;

                // totalTokens = input + output (reasoning is a subset of output, not added).
                long totalTokens = r.Input + r.Output;

                // Monthly aggregation
                monthTokens += totalTokens;
                monthCost += r.Cost;

                if (r.TimestampUtc >= weekAgo)
                {
                    weekTokens += totalTokens;
                    weekCost += r.Cost;
                    weekCachedInput += r.CachedInput;
                    weekUncachedInput += r.Input;

                    if (!weeklyModelStats.TryGetValue(entry.Model, out var stats))
                    {
                        stats = new ModelStats();
                        weeklyModelStats[entry.Model] = stats;
                    }
                    stats.Input += r.Input;
                    stats.Output += r.Output;
                    stats.CachedInput += r.CachedInput;
                    stats.Reasoning += r.Reasoning;
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

        var modelCosts = weeklyModelStats.Select(kvp => new CodexModelCost(
            kvp.Key,
            kvp.Value.Input,
            kvp.Value.Output,
            kvp.Value.CachedInput,
            kvp.Value.Reasoning,
            kvp.Value.Cost
        )).OrderByDescending(m => m.TotalCostUsd).ToList();

        return new CodexCostDetailsData(
            todayCost, todayTokens,
            weekCost, weekTokens,
            monthCost, monthTokens,
            fiveHCost, fiveHTokens,
            modelCosts)
        {
            WeekCachedInputTokens = weekCachedInput,
            WeekUncachedInputTokens = weekUncachedInput,
        };
    }

    /// <summary>
    /// Parses one session file into its resolved model and a list of usage records
    /// (per-response deltas). Only records within the 30-day monthly window are kept —
    /// older ones can never re-enter any window, so dropping them bounds cache size.
    /// </summary>
    private static async Task<(string Model, List<FileEntry> Records)> ParseFileAsync(string path)
    {
        string sessionModel = "unknown";
        var records = new List<FileEntry>();

        // The monthly cutoff moves with wall-clock; using it at parse time means a record
        // just inside the window today is kept and will naturally age out at re-fold time.
        var monthAgo = DateTime.UtcNow.AddDays(-30);

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Cheap prefilter: only lines mentioning token_count or turn_context can
                // carry the fields we need. session_meta and message bodies are huge and
                // irrelevant — skip without parsing.
                if (!line.Contains("\"token_count\"", StringComparison.Ordinal) &&
                    !line.Contains("\"turn_context\"", StringComparison.Ordinal))
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                if (!root.TryGetProperty("timestamp", out var tsEl)) continue;

                DateTime timestamp;
                if (tsEl.ValueKind == JsonValueKind.String &&
                    DateTimeOffset.TryParse(tsEl.GetString(), out var dto))
                {
                    timestamp = dto.UtcDateTime;
                }
                else
                {
                    continue;
                }

                // Capture the session model from any turn_context record.
                if (root.TryGetProperty("type", out var tcType) &&
                    tcType.ValueEquals("turn_context") &&
                    root.TryGetProperty("payload", out var tcPayload) &&
                    tcPayload.ValueKind == JsonValueKind.Object &&
                    tcPayload.TryGetProperty("model", out var modelEl) &&
                    modelEl.ValueKind == JsonValueKind.String)
                {
                    var m = modelEl.GetString();
                    if (!string.IsNullOrWhiteSpace(m))
                    {
                        // Normalise to lowercase (one anomalous "GPT-5.2" seen in the wild).
                        sessionModel = m.ToLowerInvariant();
                    }
                    continue; // turn_context carries no usage.
                }

                if (timestamp < monthAgo) continue;

                // Token usage lives on event_msg / token_count records.
                if (!root.TryGetProperty("type", out var typeEl) || !typeEl.ValueEquals("event_msg"))
                    continue;
                if (!root.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                    continue;
                if (!payload.TryGetProperty("type", out var ptype) || !ptype.ValueEquals("token_count"))
                    continue;

                if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                    continue;
                if (!info.TryGetProperty("last_token_usage", out var usage) ||
                    usage.ValueKind != JsonValueKind.Object)
                    continue;

                long input = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv) ? iv : 0;
                long output = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov) ? ov : 0;
                long cachedInput = usage.TryGetProperty("cached_input_tokens", out var ct) && ct.TryGetInt64(out var cv) ? cv : 0;
                long reasoning = usage.TryGetProperty("reasoning_output_tokens", out var rt) && rt.TryGetInt64(out var rv) ? rv : 0;

                decimal cost = CalculateCost(sessionModel, input, cachedInput, output);

                records.Add(new FileEntry(timestamp, input, output, cachedInput, reasoning, cost));
            }
        }
        catch
        {
            // Unreadable / malformed file — return whatever we parsed so far (possibly empty).
        }

        return (sessionModel, records);
    }

    /// <summary>
    /// Estimated per-token cost based on best-known OpenAI API rates (per 1M tokens).
    /// Cached input is billed at 50% of input (OpenAI's standard prompt-cache discount).
    /// Reasoning output is folded into the output rate. Rates are estimates pending
    /// verification (web tools down until 2026-08-01); refinement is a follow-up.
    /// </summary>
    private static decimal CalculateCost(string model, long input, long cachedInput, long output)
    {
        // Per 1M token rates. Default tier covers gpt-5-codex / gpt-5.1.
        decimal inPrice = 2.50m;
        decimal outPrice = 10.00m;

        if (model.Contains("5.6", StringComparison.Ordinal) ||
            model.Contains("5.5", StringComparison.Ordinal))
        {
            // Frontier (gpt-5.6-sol/terra/luna, gpt-5.5).
            inPrice = 5.00m; outPrice = 15.00m;
        }
        else if (model.Contains("5.2", StringComparison.Ordinal) ||
                 model.Contains("5.3", StringComparison.Ordinal))
        {
            inPrice = 3.00m; outPrice = 10.00m;
        }
        else if (model.Contains("5.1", StringComparison.Ordinal) ||
                 model.Contains("gpt-5-codex", StringComparison.Ordinal))
        {
            inPrice = 2.50m; outPrice = 10.00m;
        }
        else if (model.Contains("mini", StringComparison.Ordinal))
        {
            inPrice = 1.50m; outPrice = 6.00m;
        }

        // Cached input at 50% of the input rate; remainder at full input rate.
        decimal cachedPrice = inPrice * 0.5m;
        long uncachedInput = Math.Max(0, input - cachedInput);

        decimal inputCost = (uncachedInput / 1_000_000m) * inPrice
                          + (cachedInput / 1_000_000m) * cachedPrice;
        decimal outputCost = (output / 1_000_000m) * outPrice;

        return inputCost + outputCost;
    }

    /// <summary>One per-response usage record, cached.</summary>
    private sealed record FileEntry(
        DateTime TimestampUtc,
        long Input,
        long Output,
        long CachedInput,
        long Reasoning,
        decimal Cost);

    /// <summary>Cache value: the file's mtime when parsed, its resolved model, and records.</summary>
    private sealed class FileCacheEntry
    {
        public FileCacheEntry(DateTime mtime, string model, List<FileEntry> records)
        {
            Mtime = mtime;
            Model = model;
            Records = records;
        }
        public DateTime Mtime { get; }
        public string Model { get; }
        public List<FileEntry> Records { get; }
    }

    private class ModelStats
    {
        public long Input;
        public long Output;
        public long CachedInput;
        public long Reasoning;
        public decimal Cost;
    }
}
