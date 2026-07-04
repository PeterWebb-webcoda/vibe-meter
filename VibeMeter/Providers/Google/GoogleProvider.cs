using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VibeMeter.Core;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Google AI Pro / Antigravity (Gemini) usage provider. Reads the per-group, per-window
/// quota summary from Google's Cloud Code backend
/// (<c>/v1internal:retrieveUserQuotaSummary</c>) and surfaces one gauge per
/// (model-group × window) — e.g. "Gemini Weekly", "Gemini 5h", "Claude/GPT Weekly",
/// "Claude/GPT 5h". See <see cref="GoogleAuth"/> for credential discovery and
/// <c>docs/provider-research.md</c> for the full API investigation.
/// </summary>
public sealed class GoogleProvider : IUsageProvider
{
    public string Id => "google";
    public string DisplayName => "Google AI Pro";

    private readonly GoogleAuth _auth;

    // Tier-name fetch is cheap to call but rarely changes; cache briefly so the plan
    // label doesn't re-query Google every refresh tick.
    private static readonly TimeSpan TierCacheTtl = TimeSpan.FromMinutes(10);
    private static string? _cachedTierName;
    private static DateTime _tierCacheExpiryUtc = DateTime.MinValue;

    /// <summary>Production constructor.</summary>
    public GoogleProvider() : this(new GoogleAuth()) { }

    /// <summary>Testable constructor.</summary>
    public GoogleProvider(GoogleAuth auth) => _auth = auth;

    public async Task<ProviderUsage> FetchAsync()
    {
        var refresh = _auth.GetRefreshToken();
        if (string.IsNullOrWhiteSpace(refresh))
        {
            return NotConfigured(await AccountSuffixAsync());
        }

        // 1. Get a live access token. Auth failures are terminal for this fetch.
        string accessToken;
        try
        {
            accessToken = await _auth.GetAccessTokenAsync();
        }
        catch (Exception ex)
        {
            return Error($"Google auth failed: {ex.Message}");
        }

        // 2. retrieveUserQuotaSummary is the primary call — it carries the gauges.
        GoogleQuotaSummary summary;
        try
        {
            using var doc = await GoogleApiClient.RetrieveUserQuotaSummaryAsync(accessToken);
            summary = GoogleResponseParser.ParseQuotaSummary(doc);
        }
        catch (Exception ex)
        {
            return Error($"Failed to fetch Google usage: {ex.Message}");
        }

        // 3. loadCodeAssist (tier name) is optional — failure degrades to no plan label.
        string? tierError = null;
        string? tierName = _cachedTierName;
        if (tierName is null || DateTime.UtcNow > _tierCacheExpiryUtc)
        {
            try
            {
                using var tierDoc = await GoogleApiClient.LoadCodeAssistAsync(accessToken);
                tierName = GoogleResponseParser.ParseTierName(tierDoc);
                if (tierName is not null)
                {
                    _cachedTierName = tierName;
                    _tierCacheExpiryUtc = DateTime.UtcNow + TierCacheTtl;
                }
            }
            catch (Exception ex)
            {
                // Non-fatal: keep the gauges, surface a partial caveat.
                tierError = ex.Message;
            }
        }

        // 4. Build one gauge per (group × window).
        var gauges = BuildGauges(summary);
        if (gauges.Count == 0)
        {
            return Error("Google returned no quota information.");
        }

        var planLabel = await BuildPlanLabelAsync(tierName);
        return new ProviderUsage
        {
            ProviderId = Id,
            DisplayName = DisplayName,
            State = ProviderState.Ok,
            PlanLabel = planLabel,
            Gauges = gauges,
            ErrorMessage = tierError is null
                ? null
                : $"Partial — tier lookup failed: {tierError}",
        };
    }

    /// <summary>
    /// Emits one gauge per (group × window). Windows are ordered weekly-first, then 5h;
    /// groups appear in the API's order. Each gauge title is "{GroupShort} {Window}" —
    /// e.g. "Gemini Weekly", "Claude/GPT 5h".
    /// </summary>
    private static List<UsageGauge> BuildGauges(GoogleQuotaSummary summary)
    {
        var gauges = new List<UsageGauge>();
        foreach (var group in summary.Groups)
        {
            string groupShort = ShortGroupName(group.DisplayName);
            foreach (var bucket in OrderWindows(group.Buckets))
            {
                int percent = (int)Math.Round(bucket.RemainingFraction * 100);
                percent = Math.Max(0, Math.Min(100, percent));
                string windowLabel = bucket.Window.Equals("weekly", StringComparison.OrdinalIgnoreCase)
                    ? "Weekly"
                    : bucket.Window.Equals("5h", StringComparison.OrdinalIgnoreCase)
                        ? "5h"
                        : bucket.Window;

                gauges.Add(new UsageGauge(
                    Id: $"google-{groupShort.ToLowerInvariant()}-{bucket.Window}",
                    Title: $"{groupShort} {windowLabel}",
                    Subtitle: null,
                    PercentRemaining: percent,
                    ResetAt: bucket.ResetAtUtc?.ToLocalTime()));
            }
        }
        return gauges;
    }

    /// <summary>Shortens "Gemini Models" → "Gemini", "Claude and GPT models" → "Claude/GPT".</summary>
    private static string ShortGroupName(string displayName) =>
        displayName switch
        {
            var s when s.StartsWith("Gemini", StringComparison.OrdinalIgnoreCase) => "Gemini",
            var s when s.Contains("Claude", StringComparison.OrdinalIgnoreCase) &&
                       s.Contains("GPT", StringComparison.OrdinalIgnoreCase) => "Claude/GPT",
            var s when s.Contains("Claude", StringComparison.OrdinalIgnoreCase) => "Claude",
            var s when s.Contains("GPT", StringComparison.OrdinalIgnoreCase) => "GPT",
            _ => displayName.Split(' ').FirstOrDefault() ?? displayName,
        };

    /// <summary>Orders windows weekly-first, then 5h, then any others.</summary>
    private static IEnumerable<GoogleQuotaBucket> OrderWindows(IReadOnlyList<GoogleQuotaBucket> buckets)
    {
        int Rank(GoogleQuotaBucket b) => b.Window.ToLowerInvariant() switch
        {
            "weekly" => 0,
            "5h"     => 1,
            _        => 2,
        };
        return buckets.OrderBy(Rank);
    }

    private async Task<string?> BuildPlanLabelAsync(string? tierName)
    {
        var suffix = await AccountSuffixAsync();
        return tierName switch
        {
            null => suffix,
            _ when suffix is null => tierName,
            _ => $"{tierName} — {suffix}",
        };
    }

    private async Task<string?> AccountSuffixAsync()
    {
        try
        {
            return await _auth.GetAccountEmailAsync();
        }
        catch
        {
            return null;
        }
    }

    private ProviderUsage NotConfigured(string? account) => new()
    {
        ProviderId = Id,
        DisplayName = DisplayName,
        State = ProviderState.NotConfigured,
        PlanLabel = account,
        ErrorMessage =
            "Sign in to Google inside the Antigravity IDE to enable Google usage. " +
            "(Looking for " + GoogleAuth.StateDbPath + ".)"
    };

    private ProviderUsage Error(string message) => new()
    {
        ProviderId = Id,
        DisplayName = DisplayName,
        State = ProviderState.Error,
        ErrorMessage = message,
    };
}
