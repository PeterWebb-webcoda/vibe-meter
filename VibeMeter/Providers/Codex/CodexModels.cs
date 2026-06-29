using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VibeMeter.Providers.Codex;

// --- Raw DTOs for the ChatGPT/Codex backend wham API ---

public class CodexUsageResponse
{
    [JsonPropertyName("plan_type")]
    public string? PlanType { get; set; }

    [JsonPropertyName("rate_limit")]
    public CodexUsageRateLimit? RateLimit { get; set; }

    [JsonPropertyName("additional_rate_limits")]
    public List<CodexAdditionalUsageRateLimit>? AdditionalRateLimits { get; set; }

    [JsonPropertyName("rate_limit_reset_credits")]
    public CodexResetCreditCount? RateLimitResetCredits { get; set; }
}

public class CodexUsageRateLimit
{
    [JsonPropertyName("allowed")]
    public bool Allowed { get; set; }

    [JsonPropertyName("limit_reached")]
    public bool LimitReached { get; set; }

    [JsonPropertyName("primary_window")]
    public CodexUsageWindow? PrimaryWindow { get; set; }

    [JsonPropertyName("secondary_window")]
    public CodexUsageWindow? SecondaryWindow { get; set; }
}

public class CodexAdditionalUsageRateLimit
{
    [JsonPropertyName("metered_feature")]
    public string MeteredFeature { get; set; } = string.Empty;

    [JsonPropertyName("rate_limit")]
    public CodexUsageRateLimit RateLimit { get; set; } = new();
}

public class CodexUsageWindow
{
    [JsonPropertyName("used_percent")]
    public int UsedPercent { get; set; }

    [JsonPropertyName("limit_window_seconds")]
    public int LimitWindowSeconds { get; set; }

    [JsonPropertyName("reset_after_seconds")]
    public int ResetAfterSeconds { get; set; }

    [JsonPropertyName("reset_at")]
    public object? ResetAtRaw { get; set; }

    [JsonIgnore]
    public int RemainingPercent => Math.Max(0, Math.Min(100, 100 - UsedPercent));

    [JsonIgnore]
    public DateTime? ResetAt
    {
        get
        {
            if (ResetAtRaw is System.Text.Json.JsonElement element)
            {
                if (element.ValueKind == System.Text.Json.JsonValueKind.Number && element.TryGetDouble(out double timestamp))
                {
                    return DateTimeOffset.FromUnixTimeSeconds((long)timestamp).LocalDateTime;
                }
                if (element.ValueKind == System.Text.Json.JsonValueKind.String && DateTime.TryParse(element.GetString(), out DateTime date))
                {
                    return date;
                }
            }
            return null;
        }
    }
}

public class CodexResetCreditCount
{
    [JsonPropertyName("available_count")]
    public int AvailableCount { get; set; }
}

public class CodexRateLimitResetResponse
{
    [JsonPropertyName("available_count")]
    public int? AvailableCountRaw { get; set; }

    [JsonPropertyName("credits")]
    public List<CodexRateLimitResetCredit>? Credits { get; set; }

    [JsonIgnore]
    public int AvailableCount => AvailableCountRaw ?? (Credits?.Count(c => c.IsAvailable) ?? 0);
}

public class CodexRateLimitResetCredit
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "unknown";

    [JsonPropertyName("granted_at")]
    public DateTime GrantedAt { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [JsonPropertyName("redeemed_at")]
    public DateTime? RedeemedAt { get; set; }

    [JsonIgnore]
    public bool IsAvailable => Status == "available" && RedeemedAt == null;
}
