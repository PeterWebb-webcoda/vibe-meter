using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Parsed view of the <c>retrieveUserQuotaSummary</c> response: the model groups (e.g.
/// "Gemini Models", "Claude and GPT models") each carrying weekly + 5-hour quota buckets.
/// Built from a <see cref="JsonDocument"/> because the structure nests arrays-of-objects.
/// </summary>
internal sealed class GoogleQuotaSummary
{
    /// <summary>One entry per model group, in the order the API returns them.</summary>
    public IReadOnlyList<GoogleQuotaGroup> Groups { get; init; }
        = Array.Empty<GoogleQuotaGroup>();
}

/// <summary>One model group (e.g. "Gemini Models") with its buckets.</summary>
internal sealed record GoogleQuotaGroup(
    string DisplayName,
    IReadOnlyList<GoogleQuotaBucket> Buckets)
{
    /// <summary>
    /// True when this group's buckets are quota <i>windows</i> (each carries a
    /// <c>window</c> of "weekly"/"5h") rather than per-model pools. Google returns the
    /// windowed shape for subscription tiers and the per-model shape for the free
    /// "Antigravity" starter quota — see <c>GoogleProvider.BuildGauges</c>.
    /// </summary>
    public bool IsWindowed => Buckets.Any(b => !string.IsNullOrEmpty(b.Window));
}

/// <summary>
/// One quota bucket within a group. Depending on the account's tier this is either a
/// window (<c>window</c> = "weekly"/"5h", <c>displayName</c> = "Weekly Limit") or a single
/// model's pool (<c>window</c> absent, <c>displayName</c> = "Gemini 3.1 Pro (High)").
/// </summary>
internal sealed record GoogleQuotaBucket(
    string BucketId,          // "gemini-weekly" | "3p-5h" | "claude-opus-4-6-thinking"
    string Window,            // "weekly" | "5h" | "" (per-model buckets omit this)
    string DisplayName,       // "Weekly Limit" | "Gemini 3.1 Pro (High)"
    double RemainingFraction, // 0..1
    DateTime? ResetAtUtc,
    string? Description,      // human text, e.g. "You have hit your weekly limit, ..."
    bool Disabled);           // true when this limit does not currently apply

/// <summary>
/// One model's quota from <c>fetchAvailableModels</c>.
/// </summary>
internal sealed record GoogleModelQuota(
    string Key,                 // "gemini-3.6-flash-high"
    string DisplayName,         // "Gemini 3.6 Flash (High)"
    double RemainingFraction,   // 0..1
    DateTime? ResetAtUtc);

/// <summary>Parses the Cloud Code JSON responses.</summary>
internal static class GoogleResponseParser
{
    public static GoogleQuotaSummary ParseQuotaSummary(JsonDocument doc)
    {
        var groups = new List<GoogleQuotaGroup>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return new GoogleQuotaSummary();
        if (!doc.RootElement.TryGetProperty("groups", out var groupsEl) ||
            groupsEl.ValueKind != JsonValueKind.Array)
        {
            return new GoogleQuotaSummary();
        }

        foreach (var groupEl in groupsEl.EnumerateArray())
        {
            if (groupEl.ValueKind != JsonValueKind.Object) continue;

            string displayName = groupEl.TryGetProperty("displayName", out var dn) &&
                                 dn.ValueKind == JsonValueKind.String
                ? dn.GetString() ?? "Models"
                : "Models";

            var buckets = new List<GoogleQuotaBucket>();
            if (groupEl.TryGetProperty("buckets", out var bucketsEl) &&
                bucketsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var bucketEl in bucketsEl.EnumerateArray())
                {
                    var bucket = ParseBucket(bucketEl);
                    if (bucket is not null) buckets.Add(bucket);
                }
            }

            if (buckets.Count > 0)
            {
                groups.Add(new GoogleQuotaGroup(displayName, buckets));
            }
        }

        return new GoogleQuotaSummary { Groups = groups };
    }

    private static GoogleQuotaBucket? ParseBucket(JsonElement bucketEl)
    {
        if (bucketEl.ValueKind != JsonValueKind.Object) return null;

        string bucketId = bucketEl.TryGetProperty("bucketId", out var bid) &&
                          bid.ValueKind == JsonValueKind.String
            ? bid.GetString() ?? ""
            : "";

        string window = bucketEl.TryGetProperty("window", out var w) &&
                        w.ValueKind == JsonValueKind.String
            ? w.GetString() ?? ""
            : "";

        string displayName = bucketEl.TryGetProperty("displayName", out var dn) &&
                              dn.ValueKind == JsonValueKind.String
            ? dn.GetString() ?? window
            : window;

        // Absent remainingFraction means "no usage recorded" → full. Note this default is
        // why a shape change once rendered every bucket as a reassuring 100%.
        double fraction = 1.0;
        if (bucketEl.TryGetProperty("remainingFraction", out var rf) &&
            rf.ValueKind == JsonValueKind.Number &&
            rf.TryGetDouble(out var d))
        {
            fraction = d;
        }

        DateTime? resetAt = null;
        if (bucketEl.TryGetProperty("resetTime", out var rt) &&
            rt.ValueKind == JsonValueKind.String &&
            rt.GetString() is { Length: > 0 } resetStr &&
            DateTimeOffset.TryParse(resetStr, out var dto))
        {
            resetAt = dto.UtcDateTime;
        }

        string? description = bucketEl.TryGetProperty("description", out var desc) &&
                              desc.ValueKind == JsonValueKind.String
            ? desc.GetString()
            : null;

        // Google sets disabled when a limit does not currently apply — e.g. the 5-hour cap
        // once the weekly cap is exhausted. Such a bucket still reports remainingFraction 1,
        // so it must not be shown as though there were quota available.
        bool disabled = bucketEl.TryGetProperty("disabled", out var dis) &&
                        dis.ValueKind == JsonValueKind.True;

        return new GoogleQuotaBucket(
            bucketId, window, displayName, fraction, resetAt, description, disabled);
    }

    /// <summary>
    /// Parses <c>fetchAvailableModels</c> into per-model quota entries.
    /// </summary>
    /// <remarks>
    /// <b>An absent <c>remainingFraction</c> means zero, not full.</b> The response is
    /// proto3-derived JSON, which omits default-valued scalars — so an exhausted model
    /// carries a <c>quotaInfo</c> holding only its <c>resetTime</c>. Verified against a
    /// depleted account where all 20 selectable models omitted the field while the internal
    /// <c>chat_*</c>/<c>tab_*</c> entries still emitted <c>1</c>, and Antigravity's own
    /// picker flagged every one of those 20 as exhausted.
    /// <para>Entries whose <c>displayName</c> is just their key (<c>chat_20706</c>,
    /// <c>tab_flash_lite_preview</c>, …) are internal completion models rather than
    /// user-selectable ones, and always report full quota — they are skipped so they cannot
    /// mask a depleted account.</para>
    /// </remarks>
    public static IReadOnlyList<GoogleModelQuota> ParseAvailableModels(JsonDocument doc)
    {
        var list = new List<GoogleModelQuota>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return list;
        if (!doc.RootElement.TryGetProperty("models", out var modelsEl) ||
            modelsEl.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        foreach (var prop in modelsEl.EnumerateObject())
        {
            var m = prop.Value;
            if (m.ValueKind != JsonValueKind.Object) continue;

            string key = prop.Name;
            string displayName = m.TryGetProperty("displayName", out var dn) &&
                                 dn.ValueKind == JsonValueKind.String
                ? dn.GetString() ?? key
                : key;

            if (string.Equals(displayName, key, StringComparison.Ordinal)) continue;

            // No quotaInfo at all means Google is not metering this model — genuinely
            // unknown, so skip it rather than assert a figure either way.
            if (!m.TryGetProperty("quotaInfo", out var q) || q.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            double fraction = 0.0;
            if (q.TryGetProperty("remainingFraction", out var rf) &&
                rf.ValueKind == JsonValueKind.Number &&
                rf.TryGetDouble(out var d))
            {
                fraction = d;
            }

            DateTime? reset = null;
            if (q.TryGetProperty("resetTime", out var rt) &&
                rt.ValueKind == JsonValueKind.String &&
                rt.GetString() is { Length: > 0 } s &&
                DateTimeOffset.TryParse(s, out var dto))
            {
                reset = dto.UtcDateTime;
            }

            list.Add(new GoogleModelQuota(key, displayName, fraction, reset));
        }

        return list;
    }

    /// <summary>
    /// Extracts <c>cloudaicompanionProject</c> from a loadCodeAssist response — needed as
    /// the <c>project</c> argument to <c>fetchAvailableModels</c>.
    /// </summary>
    public static string? ParseProjectId(JsonDocument doc) =>
        doc.RootElement.ValueKind == JsonValueKind.Object &&
        doc.RootElement.TryGetProperty("cloudaicompanionProject", out var p) &&
        p.ValueKind == JsonValueKind.String &&
        p.GetString() is { Length: > 0 } id
            ? id
            : null;

    /// <summary>Extracts the subscription tier name from a loadCodeAssist response.</summary>
    public static string? ParseTierName(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        // Paid subscribers: paidTier.name. Free users: currentTier.name.
        foreach (var key in new[] { "paidTier", "currentTier" })
        {
            if (root.TryGetProperty(key, out var tier) &&
                tier.ValueKind == JsonValueKind.Object &&
                tier.TryGetProperty("name", out var name) &&
                name.ValueKind == JsonValueKind.String)
            {
                var s = name.GetString();
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
        }

        return null;
    }
}
