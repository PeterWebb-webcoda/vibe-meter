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
/// <b>Pricing:</b> cost is computed at fold time from a dated, explicit rate table — see
/// <see cref="RateTable"/> and <see cref="ResolveRate"/>. On this machine Claude Code
/// transcripts carry <b>no</b> <c>costUSD</c> field, so the table does 100% of the work.
/// </para>
/// <para>
/// <b>Performance:</b> to avoid re-reading the whole corpus every refresh, each file's
/// parsed records are cached keyed on its <c>LastWriteTimeUtc</c>. Only files whose mtime
/// changed are re-parsed; the rest are re-folded from cache. The cache stores <b>raw token
/// counts only</b> — cost is derived at fold time — so a rate-table change takes effect on
/// the next fold without needing to bust the cache (see the FIX-PRICING-ACCURACY brief).
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

        // 4. Re-fold all cached entries into fresh aggregates. Cost is computed here, not
        //    at parse time, so the current rate table is always applied (cache-trap fix).
        var now = DateTime.UtcNow;
        var monthAgo = now.AddDays(-30);
        var weekAgo = now.AddDays(-7);
        var fiveHoursAgo = now.AddHours(-5);
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local).Date;

        // Per-model resolved rates, memoised so the 500k-record fold stays sub-second.
        var resolvedRates = new Dictionary<string, (string MatchedId, ModelRate Rate, bool Estimated)>(StringComparer.Ordinal);

        decimal todayCost = 0, weekCost = 0, monthCost = 0, fiveHCost = 0;
        long todayTokens = 0, weekTokens = 0, monthTokens = 0, fiveHTokens = 0;
        long weekCacheRead = 0, weekCacheWrite = 0, weekUncachedInput = 0;
        var weeklyModelStats = new Dictionary<string, ModelStats>();

        // Claude writes multiple assistant rows for one streamed response. They share a
        // message id: early rows contain partial output usage and the final row contains
        // the completed usage. Merge those rows before folding or input/cache tokens are
        // billed two or three times for a single response.
        foreach (var r in DeduplicateRecords(FileCache.Values.SelectMany(e => e.Records)))
        {
            if (r.TimestampUtc < monthAgo) continue;

            // Headline token count = tokens billed at a non-trivial rate, i.e. it
            // excludes cache READS (near-free reuse at 0.1x). Cache WRITES are included
            // — they're new context billed at 1.25x. This matches the Codex headline
            // definition so the two provider panels compare honestly. See Bug 3.
            long cacheWriteTotal = r.CacheWrite5m + r.CacheWrite1h;
            long totalTokens = r.Input + r.Output + cacheWriteTotal;

            if (!resolvedRates.TryGetValue(r.Model, out var resolved))
            {
                resolved = ResolveRate(r.Model);
                resolvedRates[r.Model] = resolved;
            }
            decimal cost = r.NativeCostUsd
                ?? CalculateCost(resolved.MatchedId, resolved.Rate,
                                 r.Input, r.Output, r.CacheWrite5m, r.CacheWrite1h, r.CacheRead,
                                 r.TimestampUtc);

            // Monthly aggregation
            monthTokens += totalTokens;
            monthCost += cost;

            if (r.TimestampUtc >= weekAgo)
            {
                weekTokens += totalTokens;
                weekCost += cost;
                weekCacheRead += r.CacheRead;
                weekCacheWrite += cacheWriteTotal;
                weekUncachedInput += r.Input;

                if (!weeklyModelStats.TryGetValue(r.Model, out var stats))
                {
                    stats = new ModelStats { IsEstimated = resolved.Estimated };
                    weeklyModelStats[r.Model] = stats;
                }
                stats.Input += r.Input;
                stats.Output += r.Output;
                stats.CacheWrite += cacheWriteTotal;
                stats.CacheRead += r.CacheRead;
                stats.Cost += cost;
            }

            // Today (local-midnight boundary)
            if (TimeZoneInfo.ConvertTimeFromUtc(r.TimestampUtc, TimeZoneInfo.Local).Date == todayLocal)
            {
                todayTokens += totalTokens;
                todayCost += cost;
            }

            // 5h
            if (r.TimestampUtc >= fiveHoursAgo)
            {
                fiveHTokens += totalTokens;
                fiveHCost += cost;
            }
        }

        var modelCosts = weeklyModelStats.Select(kvp => new ClaudeModelCost(
            kvp.Key,
            kvp.Value.Input,
            kvp.Value.Output,
            kvp.Value.CacheWrite,
            kvp.Value.CacheRead,
            kvp.Value.Cost,
            kvp.Value.IsEstimated)).OrderByDescending(m => m.TotalCostUsd).ToList();

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
    /// raw token counts are captured per record (Claude's model varies per line). Records
    /// older than 30 days are dropped — they can never re-enter any window.
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
                string? messageId = msgEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString()
                    : null;

                long input = 0, output = 0, cacheRead = 0, cw5m = 0, cw1h = 0;
                if (msgEl.TryGetProperty("usage", out var usageEl))
                {
                    input = usageEl.TryGetProperty("input_tokens", out var it) ? ReadLong(it) : 0;
                    output = usageEl.TryGetProperty("output_tokens", out var ot) ? ReadLong(ot) : 0;
                    cacheRead = usageEl.TryGetProperty("cache_read_input_tokens", out var crt) ? ReadLong(crt) : 0;

                    // Cache creation comes either as a single bucket (cache_creation_input_tokens)
                    // or, in newer transcripts, split by TTL under cache_creation. If the split is
                    // present we price each tier; otherwise the whole bucket is treated as 5-minute
                    // writes (the common Claude Code case) — see Bug 5 in the fix brief.
                    long cwTotal = usageEl.TryGetProperty("cache_creation_input_tokens", out var cct) ? ReadLong(cct) : 0;
                    if (usageEl.TryGetProperty("cache_creation", out var cc) && cc.ValueKind == JsonValueKind.Object)
                    {
                        cw5m = cc.TryGetProperty("ephemeral_5m_tokens", out var m5) ? ReadLong(m5) : 0;
                        cw1h = cc.TryGetProperty("ephemeral_1h_tokens", out var h1) ? ReadLong(h1) : 0;
                    }
                    if (cw5m == 0 && cw1h == 0) cw5m = cwTotal;
                }

                // Prefer the transcript-native costUSD when present; the rate table is the
                // fallback (and on this machine does 100% of the work). The cost itself is
                // resolved at fold time, so a stale rate table never survives a re-fold.
                decimal? nativeCost = root.TryGetProperty("costUSD", out var costEl) &&
                                      costEl.TryGetDecimal(out var parsedCost)
                    ? parsedCost
                    : null;

                records.Add(new FileEntry(utcTs, messageId, model, input, output, cw5m, cw1h, cacheRead, nativeCost));
            }
        }
        catch
        {
            // Unreadable / malformed file — return whatever we parsed so far.
        }

        return records;
    }

    private static long ReadLong(JsonElement el) =>
        el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var v) ? v : 0;

    /// <summary>
    /// Merges repeated streaming snapshots for each Claude API message. Token buckets are
    /// cumulative within a message, so the maximum of each bucket is the completed usage.
    /// Rows without a message id cannot be safely matched and remain independent.
    /// </summary>
    private static IEnumerable<FileEntry> DeduplicateRecords(IEnumerable<FileEntry> records)
    {
        var byMessageId = new Dictionary<string, FileEntry>(StringComparer.Ordinal);
        var withoutMessageId = new List<FileEntry>();

        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.MessageId))
            {
                withoutMessageId.Add(record);
                continue;
            }

            if (!byMessageId.TryGetValue(record.MessageId, out var existing))
            {
                byMessageId[record.MessageId] = record;
                continue;
            }

            var latest = record.TimestampUtc >= existing.TimestampUtc ? record : existing;
            byMessageId[record.MessageId] = latest with
            {
                TimestampUtc = record.TimestampUtc >= existing.TimestampUtc
                    ? record.TimestampUtc
                    : existing.TimestampUtc,
                Input = Math.Max(existing.Input, record.Input),
                Output = Math.Max(existing.Output, record.Output),
                CacheWrite5m = Math.Max(existing.CacheWrite5m, record.CacheWrite5m),
                CacheWrite1h = Math.Max(existing.CacheWrite1h, record.CacheWrite1h),
                CacheRead = Math.Max(existing.CacheRead, record.CacheRead),
                NativeCostUsd = Max(existing.NativeCostUsd, record.NativeCostUsd),
            };
        }

        return withoutMessageId.Concat(byMessageId.Values);
    }

    private static decimal? Max(decimal? left, decimal? right) => (left, right) switch
    {
        (null, null) => null,
        (null, not null) => right,
        (not null, null) => left,
        _ => Math.Max(left!.Value, right!.Value),
    };

    // --- Pricing ----------------------------------------------------------------

    /// <summary>
    /// Anthropic published-rate source. Every table entry is verified against this page.
    /// </summary>
    private const string PricingSource = "https://platform.claude.com/docs/en/about-claude/models/overview";

    /// <summary>All rates verified 31/07/2026 against <see cref="PricingSource"/>.</summary>
    private const string LastVerified = "2026-07-31";

    /// <summary>
    /// Sonnet 5 introductory pricing of $2 in / $10 out applies through 2026-08-31; the
    /// standard $3 / $15 rate applies from 2026-09-01. Because cost is computed at fold
    /// time where the per-record timestamp is available, each record is billed by its own
    /// date — so every record inside the current rolling 7-day window bills at the intro
    /// rate, and the switchover happens automatically as records age past 1 Sept.
    /// </summary>
    private static readonly DateTime Sonnet5StandardDate = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// One model's per-million-token rates. Cache ratios follow Anthropic's standard:
    /// write 5m = 1.25x input, write 1h = 2x input, read = 0.1x input.
    /// </summary>
    /// <param name="IsVerified"><c>false</c> marks a rate as an unverified estimate so the
    /// UI can flag it (Bug 4) — every Claude entry below is verified.</param>
    private readonly record struct ModelRate(
        decimal Input,
        decimal Output,
        decimal CacheWrite5m,
        decimal CacheWrite1h,
        decimal CacheRead,
        bool IsVerified);

    /// <summary>
    /// Explicit, dated rate table keyed on canonical model ids — NOT family-word
    /// substrings. A generation that reprices will not silently inherit the old rate:
    /// an unknown id falls through to <see cref="ResolveRate"/>'s estimated fallback
    /// (Bug 2). Match order is longest-id-first so <c>claude-opus-4-8</c> wins over any
    /// shorter prefix even when a transcript id carries a date suffix.
    /// </summary>
    private static readonly (string Id, ModelRate Rate)[] RateTable =
        new (string Id, ModelRate Rate)[]
        {
            ("claude-opus-5",     new ModelRate(5.00m,  25.00m, 6.25m, 10.00m, 0.50m, true)),
            ("claude-opus-4-8",   new ModelRate(5.00m,  25.00m, 6.25m, 10.00m, 0.50m, true)),
            ("claude-opus-4-7",   new ModelRate(5.00m,  25.00m, 6.25m, 10.00m, 0.50m, true)),
            ("claude-opus-4-6",   new ModelRate(5.00m,  25.00m, 6.25m, 10.00m, 0.50m, true)),
            // Fable 5 is NOT Sonnet-priced: $10 in / $50 out — over 3x Sonnet, 2x Opus.
            ("claude-fable-5",    new ModelRate(10.00m, 50.00m, 12.50m, 20.00m, 1.00m, true)),
            ("claude-sonnet-5",   new ModelRate(3.00m,  15.00m, 3.75m,  6.00m,  0.30m, true)),
            ("claude-sonnet-4-6", new ModelRate(3.00m,  15.00m, 3.75m,  6.00m,  0.30m, true)),
            ("claude-haiku-4-5",  new ModelRate(1.00m,  5.00m,  1.25m,  2.00m,  0.10m, true)),
        }
        .OrderByDescending(t => t.Id.Length)
        .ToArray();

    /// <summary>Sonnet rates used as the estimated fallback for unknown models.</summary>
    private static readonly ModelRate FallbackRate =
        new ModelRate(3.00m, 15.00m, 3.75m, 6.00m, 0.30m, IsVerified: false);

    /// <summary>
    /// Resolves a transcript model string to its rate. Returns the matched table id, the
    /// rate, and whether it should be flagged as an estimated/estimated rate in the UI.
    /// Unknown models fall back to Sonnet pricing AND are flagged estimated (loud, not
    /// silent — Bug 2).
    /// </summary>
    private static (string MatchedId, ModelRate Rate, bool Estimated) ResolveRate(string model)
    {
        var lower = model.ToLowerInvariant();
        foreach (var (id, rate) in RateTable) // longest-id-first
        {
            if (lower.Contains(id, StringComparison.Ordinal))
                return (id, rate, Estimated: !rate.IsVerified);
        }
        return ("(estimated)", FallbackRate, Estimated: true);
    }

    /// <summary>
    /// Computes per-record cost in USD from the resolved rate and raw token counts.
    /// <para>
    /// <c>input_tokens</c> in Anthropic's usage block is already the uncached portion, so
    /// it bills at the full input rate; cache reads bill separately at 0.1x. Cache writes
    /// are split by TTL when the transcript provides the breakdown (Bug 5).
    /// </para>
    /// </summary>
    private static decimal CalculateCost(
        string matchedId,
        in ModelRate rate,
        long input,
        long output,
        long cacheWrite5m,
        long cacheWrite1h,
        long cacheRead,
        DateTime timestampUtc)
    {
        decimal inPrice = rate.Input;
        decimal outPrice = rate.Output;
        decimal cw5mPrice = rate.CacheWrite5m;
        decimal cw1hPrice = rate.CacheWrite1h;
        decimal crPrice = rate.CacheRead;

        // Sonnet 5 introductory pricing — see Sonnet5StandardDate. Cache ratios still
        // hold against the intro input rate, so re-derive the cache prices from it.
        if (matchedId == "claude-sonnet-5" && timestampUtc < Sonnet5StandardDate)
        {
            inPrice = 2.00m;
            outPrice = 10.00m;
            cw5mPrice = inPrice * 1.25m;
            cw1hPrice = inPrice * 2.00m;
            crPrice = inPrice * 0.10m;
        }

        decimal inputCost = (input / 1_000_000m) * inPrice
                          + (cacheRead / 1_000_000m) * crPrice;
        decimal outputCost = (output / 1_000_000m) * outPrice;
        decimal cacheWriteCost = (cacheWrite5m / 1_000_000m) * cw5mPrice
                               + (cacheWrite1h / 1_000_000m) * cw1hPrice;

        return inputCost + outputCost + cacheWriteCost;
    }

    /// <summary>One per-record usage entry, cached. Raw tokens only — cost is derived at fold.</summary>
    private sealed record FileEntry(
        DateTime TimestampUtc,
        string? MessageId,
        string Model,
        long Input,
        long Output,
        long CacheWrite5m,
        long CacheWrite1h,
        long CacheRead,
        decimal? NativeCostUsd);

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
        /// <summary>True when the model's rate is an estimate (unknown or unverified).</summary>
        public bool IsEstimated;
    }
}
