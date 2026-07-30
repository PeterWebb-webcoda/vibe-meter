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
/// giant <c>session_meta</c>/message lines are skipped cheaply. The cache stores <b>raw
/// token counts only</b> — cost is derived at fold time — so a rate-table change takes
/// effect on the next fold without needing to bust the cache (see the FIX-PRICING-ACCURACY
/// brief).
/// </para>
/// <para><b>Costs are API-equivalent estimates.</b> Codex subscription transcripts carry
/// no per-request $ figure, so costs are computed from OpenAI's published API rates — see
/// <see cref="ResolveRate"/>. They are not charges added to the user's subscription.
/// Reasoning output is billed at the output rate.</para>
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
        //    Cost is computed here (not at parse time) so the current rate table always
        //    applies — cache-trap fix per the FIX-PRICING-ACCURACY brief.
        var now = DateTime.UtcNow;
        var monthAgo = now.AddDays(-30);
        var weekAgo = now.AddDays(-7);
        var fiveHoursAgo = now.AddHours(-5);
        var todayLocal = TimeZoneInfo.ConvertTimeFromUtc(now, TimeZoneInfo.Local).Date;

        decimal todayCost = 0, weekCost = 0, monthCost = 0, fiveHCost = 0;
        long todayTokens = 0, weekTokens = 0, monthTokens = 0, fiveHTokens = 0;
        long weekCachedInput = 0, weekUncachedInput = 0;
        var weeklyModelStats = new Dictionary<string, ModelStats>();
        bool anyEstimated = false;

        foreach (var entry in FileCache.Values)
        {
            var resolved = ResolveRate(entry.Model);
            if (resolved.Estimated) anyEstimated = true;

            foreach (var r in entry.Records)
            {
                if (r.TimestampUtc < monthAgo) continue;

                // Headline token count excludes cached input (near-free prompt-cache reuse
                // at 10% of input). This matches the Claude headline definition (which
                // excludes cache reads) so the two provider panels compare honestly —
                // Bug 3 in the fix brief.
                long uncachedInput = Math.Max(0, r.Input - r.CachedInput);
                long totalTokens = uncachedInput + r.Output;

                decimal cost = CalculateCost(resolved.Rate, r.Input, r.CachedInput, r.Output);

                // Monthly aggregation
                monthTokens += totalTokens;
                monthCost += cost;

                if (r.TimestampUtc >= weekAgo)
                {
                    weekTokens += totalTokens;
                    weekCost += cost;
                    weekCachedInput += r.CachedInput;
                    weekUncachedInput += uncachedInput;

                    if (!weeklyModelStats.TryGetValue(entry.Model, out var stats))
                    {
                        stats = new ModelStats { IsEstimated = resolved.Estimated };
                        weeklyModelStats[entry.Model] = stats;
                    }
                    stats.Input += r.Input;
                    stats.Output += r.Output;
                    stats.CachedInput += r.CachedInput;
                    stats.Reasoning += r.Reasoning;
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
        }

        var modelCosts = weeklyModelStats.Select(kvp => new CodexModelCost(
            kvp.Key,
            kvp.Value.Input,
            kvp.Value.Output,
            kvp.Value.CachedInput,
            kvp.Value.Reasoning,
            kvp.Value.Cost,
            kvp.Value.IsEstimated)).OrderByDescending(m => m.TotalCostUsd).ToList();

        return new CodexCostDetailsData(
            todayCost, todayTokens,
            weekCost, weekTokens,
            monthCost, monthTokens,
            fiveHCost, fiveHTokens,
            modelCosts)
        {
            WeekCachedInputTokens = weekCachedInput,
            WeekUncachedInputTokens = weekUncachedInput,
            HasEstimatedRates = anyEstimated,
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
            // Codex Desktop/CLI holds active rollouts open for writing without sharing
            // delete access. File.OpenRead (FileShare.Read only) therefore rejects every
            // live task and makes today's usage appear as zero. Read the stable prefix
            // while allowing the writer to continue appending.
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
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

                records.Add(new FileEntry(timestamp, input, output, cachedInput, reasoning));
            }
        }
        catch
        {
            // Unreadable / malformed file — return whatever we parsed so far (possibly empty).
        }

        return (sessionModel, records);
    }

    // --- Pricing ----------------------------------------------------------------

    /// <summary>
    /// OpenAI published-rate source. Current model rates below were checked against the
    /// live model catalogue; legacy rates remain explicitly unverified.
    /// </summary>
    private const string PricingSource = "https://developers.openai.com/api/docs/models/compare";

    /// <summary>Rates last verified against <see cref="PricingSource"/> on this date.</summary>
    private const string LastVerified = "2026-07-31";

    /// <summary>
    /// Per 1M token rates. <c>CachedInput</c> is carried explicitly rather than derived:
    /// OpenAI's prompt-cache discount is 90% (cached input is 0.1x input), not the 50% this
    /// calculator previously assumed.
    /// </summary>
    private readonly record struct ModelRate(
        decimal Input, decimal CachedInput, decimal Output, bool IsVerified);

    /// <summary>
    /// Per-model rates keyed on explicit model ids, longest-id-first.
    /// </summary>
    /// <remarks>
    /// <para>Matching on <c>"5.6"</c> was actively wrong: the three 5.6 variants differ by up
    /// to 5x on input (sol 5.00, terra 2.50, luna 1.00), so one substring priced every Luna
    /// turn as though it were Sol.</para>
    /// <para>The 5.1/5.2/5.3/codex entries are legacy — no longer on OpenAI's rate card, and
    /// unused by any session in the last 30 days. Kept unverified so old transcripts still
    /// produce a number, flagged as an estimate in the UI.</para>
    /// </remarks>
    private static readonly (string Id, ModelRate Rate)[] RateTable =
        new (string Id, ModelRate Rate)[]
        {
            // Verified 2026-07-31 against the live OpenAI model catalogue. OpenAI reduced
            // Terra and Luna after the original 27/07 table was added.
            ("gpt-5.6-sol",   new ModelRate(5.00m,  0.50m,   30.00m, true)),
            ("gpt-5.6-terra", new ModelRate(2.00m,  0.20m,   12.00m, true)),
            ("gpt-5.6-luna",  new ModelRate(0.20m,  0.02m,    1.20m, true)),
            // The unsuffixed alias routes to Sol. Keep this after the specific ids.
            ("gpt-5.6",       new ModelRate(5.00m,  0.50m,   30.00m, true)),
            ("gpt-5.5-pro",   new ModelRate(30.00m, 3.00m,  180.00m, true)),
            ("gpt-5.5",       new ModelRate(5.00m,  0.50m,   30.00m, true)),
            ("gpt-5.4-nano",  new ModelRate(0.20m,  0.02m,    1.25m, true)),
            ("gpt-5.4-mini",  new ModelRate(0.75m,  0.075m,   4.50m, true)),
            ("gpt-5.4-pro",   new ModelRate(30.00m, 3.00m,  180.00m, true)),
            ("gpt-5.4",       new ModelRate(2.50m,  0.25m,   15.00m, true)),

            // Legacy — not on the current rate card; unverified.
            ("gpt-5.3",       new ModelRate(3.00m,  0.30m,   10.00m, false)),
            ("gpt-5.2",       new ModelRate(3.00m,  0.30m,   10.00m, false)),
            ("gpt-5.1",       new ModelRate(2.50m,  0.25m,   10.00m, false)),
            ("gpt-5-codex",   new ModelRate(2.50m,  0.25m,   10.00m, false)),
        };

    /// <summary>Unknown model — priced at the mid tier and always surfaced as an estimate.</summary>
    private static readonly ModelRate DefaultRate = new(2.00m, 0.20m, 12.00m, false);

    private static (ModelRate Rate, bool Estimated) ResolveRate(string model)
    {
        foreach (var (id, rate) in RateTable) // longest-id-first
        {
            if (model.Contains(id, StringComparison.OrdinalIgnoreCase))
                return (rate, Estimated: !rate.IsVerified);
        }
        return (DefaultRate, Estimated: true);
    }

    /// <summary>Per-record cost in USD from the resolved rate and raw token counts.</summary>
    /// <remarks>
    /// <para>OpenAI's <c>input_tokens</c> is inclusive of <c>cached_input_tokens</c>, so the
    /// uncached remainder is the difference. This is the opposite of Anthropic, whose
    /// <c>input_tokens</c> already excludes cache reads — see <c>ClaudeCostCalculator</c>.</para>
    /// <para><c>reasoning_output_tokens</c> is deliberately not added: OpenAI counts reasoning
    /// inside <c>output_tokens</c>, so adding it would double-charge.</para>
    /// <para>Two published rules are not modelled. Cache writes bill at 1.25x input, but
    /// <c>cache_write_input_tokens</c> is zero on every turn on this machine (24,472
    /// observations), so there is nothing to charge. Prompts over 272K input tokens bill at 2x
    /// input / 1.5x output for the whole request — 1.27% of turns here, largest seen 327K — but
    /// how cached tokens are treated under that tier isn't documented clearly enough to
    /// implement without guessing, so it is left out and recorded here instead.</para>
    /// </remarks>
    private static decimal CalculateCost(in ModelRate rate, long input, long cachedInput, long output)
    {
        long uncachedInput = Math.Max(0, input - cachedInput);

        decimal inputCost = (uncachedInput / 1_000_000m) * rate.Input
                          + (cachedInput / 1_000_000m) * rate.CachedInput;
        decimal outputCost = (output / 1_000_000m) * rate.Output;

        return inputCost + outputCost;
    }

    /// <summary>One per-response usage record, cached. Raw tokens only — cost is derived at fold.</summary>
    private sealed record FileEntry(
        DateTime TimestampUtc,
        long Input,
        long Output,
        long CachedInput,
        long Reasoning);

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
        /// <summary>True when the model's rate is an estimate (unknown or unverified).</summary>
        public bool IsEstimated;
    }
}
