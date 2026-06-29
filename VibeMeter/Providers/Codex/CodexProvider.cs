using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VibeMeter.Core;

namespace VibeMeter.Providers.Codex;

/// <summary>
/// Codex / OpenAI usage provider. Reads the local Codex sign-in, calls the
/// ChatGPT backend wham API, and normalises the result into a
/// <see cref="ProviderUsage"/>.
/// </summary>
public sealed class CodexProvider : IUsageProvider
{
    public string Id => "codex";
    public string DisplayName => "Codex";

    private readonly CodexAuth _auth;
    private readonly Func<CodexApiClient> _clientFactory;

    /// <summary>Production constructor: uses the real auth file and a fresh HTTP client.</summary>
    public CodexProvider() : this(new CodexAuth()) { }

    /// <summary>Testable constructor.</summary>
    public CodexProvider(CodexAuth auth)
    {
        _auth = auth;
        _clientFactory = () => new CodexApiClient(auth);
    }

    public async Task<ProviderUsage> FetchAsync()
    {
        string? token;
        try
        {
            token = await _auth.GetAccessTokenAsync();
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }

        if (string.IsNullOrEmpty(token))
        {
            return new ProviderUsage
            {
                ProviderId = Id,
                DisplayName = DisplayName,
                State = ProviderState.NotConfigured,
                ErrorMessage = $"Sign in to Codex on this PC (creates {CodexAuth.AuthFilePath}), then refresh."
            };
        }

        CodexUsageResponse? usage = null;
        CodexRateLimitResetResponse? credits = null;
        string? usageError = null;
        string? creditsError = null;

        using var client = _clientFactory();
        try { usage = await client.GetUsageAsync(token); }
        catch (Exception ex) { usageError = ex.Message; }

        try { credits = await client.GetRateLimitResetCreditsAsync(token); }
        catch (Exception ex) { creditsError = ex.Message; }

        // Both failed → surface the error.
        if (usage == null && credits == null)
        {
            return Error(usageError ?? creditsError ?? "Unknown Codex error.");
        }

        var gauges = new List<UsageGauge>();
        string? planLabel = usage?.PlanType;
        string? resetNote = null;

        if (usage?.RateLimit?.PrimaryWindow is CodexUsageWindow primary)
        {
            gauges.Add(new UsageGauge(
                Id: "codex-primary",
                Title: GetDurationTitle(primary.LimitWindowSeconds),
                Subtitle: DisplayName,
                PercentRemaining: primary.RemainingPercent,
                ResetAt: primary.ResetAt));
        }

        if (usage?.RateLimit?.SecondaryWindow is CodexUsageWindow secondary)
        {
            gauges.Add(new UsageGauge(
                Id: "codex-weekly",
                Title: "Weekly",
                Subtitle: DisplayName,
                PercentRemaining: secondary.RemainingPercent,
                ResetAt: secondary.ResetAt));

            if (secondary.ResetAt.HasValue)
            {
                resetNote = $"Weekly reset: {secondary.ResetAt.Value:MMM d, h:mm tt}";
            }
        }

        // Codex-Spark (Bengal Fox) sub-feature, if present.
        var spark = usage?.AdditionalRateLimits?
            .FirstOrDefault(a => a.MeteredFeature == "codex_bengalfox");
        if (spark?.RateLimit?.PrimaryWindow is CodexUsageWindow sparkPrimary)
        {
            gauges.Add(new UsageGauge(
                Id: "codex-spark",
                Title: "Spark",
                Subtitle: "5h limit",
                PercentRemaining: sparkPrimary.RemainingPercent,
                ResetAt: sparkPrimary.ResetAt));
        }

        int? availableCount = usage?.RateLimitResetCredits?.AvailableCount;
        if (availableCount is null && credits != null)
        {
            availableCount = credits.AvailableCount;
        }

        string? errorMessage = (usageError, creditsError) switch
        {
            (not null, not null) => usageError,
            (not null, null)    => $"Partial — {usageError}",
            (null, not null)    => $"Partial — {creditsError}",
            _                   => null
        };

        return new ProviderUsage
        {
            ProviderId = Id,
            DisplayName = DisplayName,
            State = ProviderState.Ok,
            PlanLabel = planLabel,
            AvailableCount = availableCount,
            ResetNote = resetNote,
            Gauges = gauges,
            ErrorMessage = errorMessage
        };
    }

    private ProviderUsage Error(string message) => new()
    {
        ProviderId = Id,
        DisplayName = DisplayName,
        State = ProviderState.Error,
        ErrorMessage = message
    };

    private static string GetDurationTitle(int limitWindowSeconds)
    {
        if (limitWindowSeconds >= 604_800) return "Weekly";
        if (limitWindowSeconds >= 86_400) return $"{limitWindowSeconds / 86_400}d";
        return $"{Math.Max(1, limitWindowSeconds / 3_600)}h";
    }
}
