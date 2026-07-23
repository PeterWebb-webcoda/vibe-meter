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
/// individual lines are tens of KB). Lines are streamed and substring-prefiltered before
/// JSON parsing so the giant <c>session_meta</c>/message lines are skipped cheaply.
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

    public static async Task<CodexCostDetailsData?> CalculateCostsAsync()
    {
        if (!Directory.Exists(SessionsDir))
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
        long weekCachedInput = 0, weekUncachedInput = 0;

        var weeklyModelStats = new Dictionary<string, ModelStats>();

        foreach (var file in Directory.EnumerateFiles(SessionsDir, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                // First pass model resolution is done inline while streaming: read the
                // session's turn_context model once, then attribute every token_count
                // in the file to it.
                string sessionModel = "unknown";

                using var stream = File.OpenRead(file);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Cheap prefilter: only lines mentioning token_count or turn_context
                    // can carry the fields we need. session_meta and message bodies are
                    // huge and irrelevant here — skip them without parsing.
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

                    var utcTs = timestamp;

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
                        continue; // turn_context carries no usage; nothing else to do.
                    }

                    if (utcTs < monthAgo) continue;

                    // Token usage lives on event_msg / token_count records.
                    if (!root.TryGetProperty("type", out var typeEl) || !typeEl.ValueEquals("event_msg"))
                        continue;
                    if (!root.TryGetProperty("payload", out var payload) ||
                        payload.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!payload.TryGetProperty("type", out var ptype) || !ptype.ValueEquals("token_count"))
                        continue;

                    // payload.info can be null on older records — skip those.
                    if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
                        continue;
                    if (!info.TryGetProperty("last_token_usage", out var usage) ||
                        usage.ValueKind != JsonValueKind.Object)
                        continue;

                    long input = usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt64(out var iv) ? iv : 0;
                    long output = usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt64(out var ov) ? ov : 0;
                    long cachedInput = usage.TryGetProperty("cached_input_tokens", out var ct) && ct.TryGetInt64(out var cv) ? cv : 0;
                    long reasoning = usage.TryGetProperty("reasoning_output_tokens", out var rt) && rt.TryGetInt64(out var rv) ? rv : 0;

                    // Codex output already includes reasoning tokens (reasoning is a subset
                    // breakdown of output, not additive). Do not add reasoning again.
                    long totalTokens = input + output;
                    decimal cost = CalculateCost(sessionModel, input, cachedInput, output);

                    // Monthly aggregation
                    monthTokens += totalTokens;
                    monthCost += cost;

                    if (utcTs >= weekAgo)
                    {
                        weekTokens += totalTokens;
                        weekCost += cost;
                        weekCachedInput += cachedInput;
                        weekUncachedInput += input;

                        if (!weeklyModelStats.TryGetValue(sessionModel, out var stats))
                        {
                            stats = new ModelStats();
                            weeklyModelStats[sessionModel] = stats;
                        }
                        stats.Input += input;
                        stats.Output += output;
                        stats.CachedInput += cachedInput;
                        stats.Reasoning += reasoning;
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
            catch
            {
                // Ignore unreadable files or malformed JSON.
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

    private class ModelStats
    {
        public long Input;
        public long Output;
        public long CachedInput;
        public long Reasoning;
        public decimal Cost;
    }
}
