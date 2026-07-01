using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace VibeMeter.Providers.Claude;

// --- Raw DTOs for the Claude Code local cache files ---
//
// usage_cache.json is written by the Claude Code CLI itself (the same data the
// /usage command shows). It holds utilisation for the 5-hour rolling window, the
// 7-day window, model-scoped variants, and a structured limits array. Reading it
// avoids any need to handle the OAuth bearer token / refresh dance — Claude Code
// keeps this cache fresh while it runs.

public class ClaudeUsageCacheFile
{
    [JsonPropertyName("timestamp")]
    public string? Timestamp { get; set; }

    [JsonPropertyName("data")]
    public ClaudeUsageData? Data { get; set; }
}

public class ClaudeUsageData
{
    /// <summary>5-hour rolling window utilisation.</summary>
    [JsonPropertyName("five_hour")]
    public ClaudeUsageWindow? FiveHour { get; set; }

    /// <summary>7-day rolling window utilisation.</summary>
    [JsonPropertyName("seven_day")]
    public ClaudeUsageWindow? SevenDay { get; set; }

    /// <summary>Structured limit windows (session / weekly_all / weekly_scoped).</summary>
    [JsonPropertyName("limits")]
    public List<ClaudeUsageLimit>? Limits { get; set; }
}

public class ClaudeUsageWindow
{
    /// <summary>Percentage of the window already used (0–100).</summary>
    [JsonPropertyName("utilization")]
    public double? Utilization { get; set; }

    [JsonPropertyName("resets_at")]
    public string? ResetsAt { get; set; }

    [JsonIgnore]
    public int UsedPercent => Utilization.HasValue
        ? Math.Max(0, Math.Min(100, (int)Math.Round(Utilization.Value)))
        : 0;

    [JsonIgnore]
    public DateTime? ResetAt => ClaudeJson.ParseIso(ResetsAt);
}

public class ClaudeUsageLimit
{
    /// <summary>"session", "weekly_all", or "weekly_scoped".</summary>
    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("group")]
    public string? Group { get; set; }

    /// <summary>Percentage of the window already used (0–100).</summary>
    [JsonPropertyName("percent")]
    public int? Percent { get; set; }

    /// <summary>"normal", "warning", or "critical".</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("resets_at")]
    public string? ResetsAt { get; set; }

    /// <summary>Present on "weekly_scoped" limits — identifies what the limit applies to.</summary>
    [JsonPropertyName("scope")]
    public ClaudeLimitScope? Scope { get; set; }

    [JsonPropertyName("is_active")]
    public bool? IsActive { get; set; }

    [JsonIgnore]
    public DateTime? ResetAt => ClaudeJson.ParseIso(ResetsAt);
}

public class ClaudeLimitScope
{
    [JsonPropertyName("model")]
    public ClaudeLimitScopeModel? Model { get; set; }

    [JsonPropertyName("surface")]
    public string? Surface { get; set; }
}

public class ClaudeLimitScopeModel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>e.g. "Fable" — the model family this weekly limit is scoped to.</summary>
    [JsonPropertyName("display_name")]
    public string? DisplayName { get; set; }
}

// --- Account / plan metadata read from ~/.claude.json (oauthAccount block) ---
// Contains no secrets — only identity, plan tier and organisation metadata.

public class ClaudeSettingsFile
{
    [JsonPropertyName("oauthAccount")]
    public ClaudeOAuthAccount? OauthAccount { get; set; }
}

public class ClaudeOAuthAccount
{
    [JsonPropertyName("emailAddress")]
    public string? EmailAddress { get; set; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("organizationName")]
    public string? OrganizationName { get; set; }

    /// <summary>e.g. "claude_team", "claude_individual".</summary>
    [JsonPropertyName("organizationType")]
    public string? OrganizationType { get; set; }

    [JsonPropertyName("seatTier")]
    public string? SeatTier { get; set; }

    /// <summary>e.g. "default_claude_max_5x".</summary>
    [JsonPropertyName("userRateLimitTier")]
    public string? UserRateLimitTier { get; set; }

    [JsonPropertyName("hasExtraUsageEnabled")]
    public bool? HasExtraUsageEnabled { get; set; }
}

/// <summary>Shared JSON parsing helpers for the Claude provider.</summary>
internal static class ClaudeJson
{
    /// <summary>Parses an ISO-8601 timestamp (with or without offset) to local time.</summary>
    public static DateTime? ParseIso(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out DateTime result)
            ? result.ToLocalTime()
            : null;
    }
}
