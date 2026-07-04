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

/// <summary>One model group (e.g. "Gemini Models") with its weekly + 5-hour buckets.</summary>
internal sealed record GoogleQuotaGroup(
    string DisplayName,
    IReadOnlyList<GoogleQuotaBucket> Buckets);

/// <summary>One quota window within a group (weekly or 5h).</summary>
internal sealed record GoogleQuotaBucket(
    string Window,            // "weekly" | "5h"
    string DisplayName,       // "Weekly Limit" | "Five Hour Limit"
    double RemainingFraction, // 0..1
    DateTime? ResetAtUtc);

/// <summary>Parses the <c>retrieveUserQuotaSummary</c> JSON response.</summary>
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

        string window = bucketEl.TryGetProperty("window", out var w) &&
                        w.ValueKind == JsonValueKind.String
            ? w.GetString() ?? ""
            : "";

        string displayName = bucketEl.TryGetProperty("displayName", out var dn) &&
                              dn.ValueKind == JsonValueKind.String
            ? dn.GetString() ?? window
            : window;

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

        return new GoogleQuotaBucket(window, displayName, fraction, resetAt);
    }

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
