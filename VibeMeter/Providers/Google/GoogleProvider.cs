using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using VibeMeter.Core;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Google AI Pro / Antigravity (Gemini) usage provider. Reads the quota summary from
/// Google's Cloud Code backend (<c>/v1internal:retrieveUserQuotaSummary</c>) and surfaces
/// it as gauges.
/// </summary>
/// <remarks>
/// <para><b>Two response shapes.</b> Google returns a different structure per tier, and both
/// must be handled — see <see cref="BuildGauges"/>:</para>
/// <list type="bullet">
/// <item><b>Windowed</b> (subscription tiers): one group per model family ("Gemini Models",
/// "Claude and GPT models"), each with a <c>weekly</c> and a <c>5h</c> bucket.</item>
/// <item><b>Per-model</b> (free "Antigravity" starter quota): a single "All Models" group
/// whose buckets are individual models with no <c>window</c> field.</item>
/// </list>
/// <para><b>Multi-account carousel.</b> The card shows one account at a time. The roster is
/// the account Antigravity itself is signed into (auto-detected) plus any accounts added via
/// Settings → "Add Google account", de-duped by email — so the IDE's account is never hidden
/// just because another was added. <see cref="CycleNextAccount"/> /
/// <see cref="CyclePrevAccount"/> move through it.</para>
/// </remarks>
public sealed class GoogleProvider : IUsageProvider
{
    public string Id => "google";
    public string DisplayName => "Google AI Pro";

    private readonly GoogleAuth _auth;

    /// <summary>
    /// Accounts explicitly added through VibeMeter's OAuth flow, set by the view model from
    /// settings. Static so the value survives provider re-instantiation each refresh cycle.
    /// </summary>
    public static List<GoogleAccount> ConfiguredAccounts { get; set; } = new();

    /// <summary>
    /// The full carousel roster (auto-detected Antigravity account + configured accounts),
    /// as resolved by the last fetch. The UI reads <c>Count</c> to decide whether to show
    /// the carousel controls.
    /// </summary>
    public static IReadOnlyList<GoogleAccount> Accounts { get; private set; } = Array.Empty<GoogleAccount>();

    /// <summary>Index into <see cref="Accounts"/> for the currently-displayed account.</summary>
    public static int ActiveAccountIndex { get; private set; }

    /// <summary>Event fired when the active account changes (so the VM can re-fetch).</summary>
    public static event Action? ActiveAccountChanged;

    // The loadCodeAssist lookup (tier name + project id) is cheap but rarely changes;
    // cache per-account so each account's values are remembered without re-querying.
    private static readonly TimeSpan TierCacheTtl = TimeSpan.FromMinutes(10);
    private static readonly Dictionary<string, (string? Tier, string? Project, DateTime ExpiryUtc)> TierCache
        = new(StringComparer.Ordinal);

    // Resolving the auto-detected account's email costs a token exchange + userinfo call, so
    // cache it per refresh token — it never changes for a given token.
    private static readonly Dictionary<string, string> AutoDetectedEmailCache
        = new(StringComparer.Ordinal);

    /// <summary>Production constructor.</summary>
    public GoogleProvider() : this(new GoogleAuth()) { }

    /// <summary>Testable constructor.</summary>
    public GoogleProvider(GoogleAuth auth) => _auth = auth;

    /// <summary>Advances to the next account in the carousel (wraps). No-op below 2 accounts.</summary>
    public static void CycleNextAccount() => Step(+1);

    /// <summary>Steps back to the previous account in the carousel (wraps). No-op below 2 accounts.</summary>
    public static void CyclePrevAccount() => Step(-1);

    private static void Step(int delta)
    {
        int count = Accounts.Count;
        if (count < 2) return;
        ActiveAccountIndex = ((ActiveAccountIndex + delta) % count + count) % count;
        ActiveAccountChanged?.Invoke();
    }

    public async Task<ProviderUsage> FetchAsync()
    {
        var roster = await ResolveRosterAsync();
        Accounts = roster;

        if (roster.Count == 0)
        {
            return NotConfigured();
        }

        // Clamp the active index in case accounts were removed since the last fetch.
        if (ActiveAccountIndex >= roster.Count) ActiveAccountIndex = 0;
        var active = roster[ActiveAccountIndex];

        // 1. Get a live access token for the chosen account.
        string accessToken;
        try
        {
            accessToken = await _auth.GetAccessTokenForAccountAsync(active.RefreshToken);
        }
        catch (Exception ex)
        {
            return Error($"Google auth failed for {active.Email}: {ex.Message}");
        }

        // 2. retrieveUserQuotaSummary carries the gauges.
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

        // 3. loadCodeAssist supplies the tier name (plan label) and the project id needed by
        //    fetchAvailableModels. Optional — failure degrades to no plan label.
        string? tierError = null;
        string? tierName = null;
        string? projectId = null;
        string tierCacheKey = active.Email.Length > 0 ? active.Email : "default";
        if (TierCache.TryGetValue(tierCacheKey, out var entry) && entry.ExpiryUtc > DateTime.UtcNow)
        {
            (tierName, projectId, _) = entry;
        }
        else
        {
            try
            {
                using var tierDoc = await GoogleApiClient.LoadCodeAssistAsync(accessToken);
                tierName = GoogleResponseParser.ParseTierName(tierDoc);
                projectId = GoogleResponseParser.ParseProjectId(tierDoc);
                TierCache[tierCacheKey] = (tierName, projectId, DateTime.UtcNow + TierCacheTtl);
            }
            catch (Exception ex)
            {
                tierError = ex.Message;
            }
        }

        // 4. Build gauges from whichever source is trustworthy for this account.
        var gauges = summary.Groups.Any(g => g.IsWindowed)
            ? BuildGauges(summary)
            : await BuildGaugesFromModelsAsync(accessToken, projectId);

        if (gauges.Count == 0)
        {
            return new ProviderUsage
            {
                ProviderId = Id,
                DisplayName = DisplayName,
                State = ProviderState.Ok,
                PlanLabel = BuildPlanLabel(tierName, active),
                Notice =
                    "Google returned no usable quota figures for this account" +
                    $"{(tierName is null ? "" : $" ({tierName})")}. Check the model picker in " +
                    "Antigravity for live limits.",
            };
        }

        return new ProviderUsage
        {
            ProviderId = Id,
            DisplayName = DisplayName,
            State = ProviderState.Ok,
            PlanLabel = BuildPlanLabel(tierName, active),
            Gauges = gauges,
            ErrorMessage = tierError is null
                ? null
                : $"Partial — tier lookup failed: {tierError}",
        };
    }

    /// <summary>
    /// Builds gauges from <c>fetchAvailableModels</c>, used when <c>retrieveUserQuotaSummary</c>
    /// is not the windowed shape.
    /// </summary>
    /// <remarks>
    /// On the free "Antigravity Starter Quota" tier the quota summary is a placeholder: every
    /// bucket is pinned at <c>remainingFraction: 1</c> and each <c>resetTime</c> is recomputed
    /// as "now + 7 days" on every request (polling the same account 45 seconds apart moved the
    /// reset by 46 seconds). Rendering it produced full green rings for an account that was in
    /// fact exhausted. <c>fetchAvailableModels</c> reports that account honestly — real fixed
    /// reset times matching Antigravity's own picker, and zero remaining — so it is the source
    /// of truth here.
    /// </remarks>
    private static async Task<List<UsageGauge>> BuildGaugesFromModelsAsync(
        string accessToken, string? projectId)
    {
        IReadOnlyList<GoogleModelQuota> models;
        try
        {
            using var doc = await GoogleApiClient.FetchAvailableModelsAsync(accessToken, projectId);
            models = GoogleResponseParser.ParseAvailableModels(doc);
        }
        catch
        {
            // Caller renders the "no usable figures" notice rather than inventing gauges.
            return new List<UsageGauge>();
        }

        var gauges = new List<UsageGauge>();
        var families = models
            .GroupBy(m => ModelFamily(m.Key, m.DisplayName), StringComparer.Ordinal)
            .OrderBy(g => FamilyRank(g.Key))
            .ThenBy(g => g.Key, StringComparer.Ordinal);

        foreach (var family in families)
        {
            var ordered = family.OrderBy(m => m.RemainingFraction).ToList();
            var worst = ordered[0];

            var lines = new List<string> { $"{family.Key} — lowest remaining pool:" };
            foreach (var m in ordered)
            {
                lines.Add($"  {m.DisplayName}: {ToPercent(m.RemainingFraction)}%");
            }

            gauges.Add(new UsageGauge(
                Id: $"google-family-{Slug(family.Key)}",
                Title: family.Key,
                Subtitle: null,
                PercentRemaining: ToPercent(worst.RemainingFraction),
                ResetAt: IsRollingReset(worst.ResetAtUtc) ? null : worst.ResetAtUtc?.ToLocalTime(),
                TooltipText: string.Join("\n", lines)));
        }

        return gauges;
    }

    /// <summary>
    /// True when a reset time is the rolling "now + 7 days" placeholder rather than a real
    /// boundary. Such a value shifts on every refresh, so it is better omitted than shown.
    /// </summary>
    private static bool IsRollingReset(DateTime? resetUtc) =>
        resetUtc is { } r &&
        Math.Abs((r - DateTime.UtcNow - TimeSpan.FromDays(7)).TotalMinutes) < 10;

    /// <summary>
    /// Builds the carousel roster: the Antigravity-detected account first (so the IDE's
    /// signed-in account is always visible), then the explicitly-configured accounts,
    /// de-duped by email. A configured entry wins over the auto-detected one for the same
    /// email, because its token was minted with VibeMeter's own consent.
    /// </summary>
    private async Task<List<GoogleAccount>> ResolveRosterAsync()
    {
        var configured = ConfiguredAccounts
            .Where(a => !string.IsNullOrWhiteSpace(a.RefreshToken))
            .ToList();

        var roster = new List<GoogleAccount>();

        var autoToken = _auth.GetRefreshToken();
        if (!string.IsNullOrWhiteSpace(autoToken))
        {
            string email = await ResolveAutoDetectedEmailAsync(autoToken!);
            bool alreadyConfigured = configured.Any(a =>
                string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));

            if (!alreadyConfigured)
            {
                roster.Add(new GoogleAccount
                {
                    Email = email,
                    RefreshToken = autoToken!,
                    IsAutoDetected = true,
                });
            }
        }

        roster.AddRange(configured);
        return roster;
    }

    /// <summary>
    /// Resolves the email behind the Antigravity refresh token. Prefers the authoritative
    /// userinfo endpoint (the IDE's own account file can name a different account than the
    /// token actually belongs to), falling back to that file, then to a generic label.
    /// </summary>
    private async Task<string> ResolveAutoDetectedEmailAsync(string refreshToken)
    {
        if (AutoDetectedEmailCache.TryGetValue(refreshToken, out var cached)) return cached;

        string? email = null;
        try
        {
            var accessToken = await _auth.GetAccessTokenForAccountAsync(refreshToken);
            email = await GoogleAuth.GetUserEmailAsync(accessToken);
        }
        catch
        {
            // Fall through to the local account file.
        }

        email ??= await _auth.GetAccountEmailAsync();
        email ??= "Antigravity account";

        AutoDetectedEmailCache[refreshToken] = email;
        return email;
    }

    /// <summary>
    /// Builds the plan label: the tier plus the account email. The carousel controls show
    /// the position, so it is deliberately not repeated here.
    /// </summary>
    private static string? BuildPlanLabel(string? tierName, GoogleAccount account)
    {
        string? accountPart = account.Email.Length > 0 ? account.Email : null;

        return (tierName, accountPart) switch
        {
            (null, null) => null,
            (null, _)    => accountPart,
            (_, null)    => tierName,
            _            => $"{tierName} — {accountPart}",
        };
    }

    /// <summary>
    /// Turns the windowed quota summary into gauges — one per (family × window), e.g.
    /// "Gemini Weekly".
    /// <para>Buckets Google marks <c>disabled</c> are skipped: such a limit does not
    /// currently apply and still reports 100%, which would read as free quota.</para>
    /// <para>Non-windowed groups are ignored here; that shape is a placeholder and is
    /// served by <see cref="BuildGaugesFromModelsAsync"/> instead.</para>
    /// </summary>
    private static List<UsageGauge> BuildGauges(GoogleQuotaSummary summary)
    {
        var gauges = new List<UsageGauge>();

        foreach (var group in summary.Groups.Where(g => g.IsWindowed))
        {
            gauges.AddRange(BuildWindowedGauges(group));
        }

        return gauges;
    }

    /// <summary>One gauge per window ("Gemini 5h", "Gemini Weekly") for a windowed group.</summary>
    private static IEnumerable<UsageGauge> BuildWindowedGauges(GoogleQuotaGroup group)
    {
        string family = ShortGroupName(group.DisplayName);

        foreach (var bucket in OrderWindows(group.Buckets))
        {
            if (bucket.Disabled) continue;

            string windowLabel = bucket.Window.ToLowerInvariant() switch
            {
                "weekly" => "Weekly",
                "5h"     => "5h",
                _        => bucket.Window,
            };

            yield return new UsageGauge(
                Id: $"google-{Slug(family)}-{bucket.Window}",
                Title: $"{family} {windowLabel}".Trim(),
                Subtitle: null,
                PercentRemaining: ToPercent(bucket.RemainingFraction),
                ResetAt: bucket.ResetAtUtc?.ToLocalTime(),
                TooltipText: BuildTooltip($"{group.DisplayName} — {bucket.DisplayName}", bucket.Description));
        }
    }

    /// <summary>
    /// Buckets a model into a display family, e.g. "Gemini 3.1 Pro (High)" → "Gemini Pro",
    /// "Claude Opus 4.6 (Thinking)" → "Claude", "GPT-OSS 120B" → "GPT".
    /// </summary>
    private static string ModelFamily(string modelKey, string displayName)
    {
        // The key is the stabler signal ("gemini-3.1-pro-low", "claude-opus-4-6-thinking").
        string key = (modelKey.Length > 0 ? modelKey : displayName).ToLowerInvariant();

        if (key.Contains("claude")) return "Claude";
        if (key.Contains("gpt")) return "GPT";
        if (key.Contains("gemini"))
        {
            if (key.Contains("pro")) return "Gemini Pro";
            if (key.Contains("flash")) return "Gemini Flash";
            return "Gemini";
        }

        // Unknown model — fall back to its own display name so it is still surfaced.
        return displayName.Length > 0 ? displayName : "Models";
    }

    /// <summary>Fixed display order for model families, so the card doesn't reshuffle.</summary>
    private static int FamilyRank(string family) => family switch
    {
        "Gemini Pro"   => 0,
        "Gemini Flash" => 1,
        "Gemini"       => 2,
        "Claude"       => 3,
        "GPT"          => 4,
        _              => 5,
    };

    /// <summary>Lower-cases and hyphenates a display name for use in a gauge id.</summary>
    private static string Slug(string name) =>
        name.ToLowerInvariant().Replace(' ', '-').Replace('/', '-');

    private static int ToPercent(double fraction) =>
        Math.Clamp((int)Math.Round(fraction * 100), 0, 100);

    private static string? BuildTooltip(string header, string? description) =>
        string.IsNullOrWhiteSpace(description) ? header : $"{header}\n\n{description}";

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

    /// <summary>Orders windows 5h-first, then weekly, then any others.</summary>
    private static IEnumerable<GoogleQuotaBucket> OrderWindows(IReadOnlyList<GoogleQuotaBucket> buckets)
    {
        int Rank(GoogleQuotaBucket b) => b.Window.ToLowerInvariant() switch
        {
            "5h"     => 0,
            "weekly" => 1,
            _        => 2,
        };
        return buckets.OrderBy(Rank);
    }

    private ProviderUsage NotConfigured() => new()
    {
        ProviderId = Id,
        DisplayName = DisplayName,
        State = ProviderState.NotConfigured,
        ErrorMessage =
            "No Google account configured. Open Settings → Add Google account to sign in, " +
            "or sign in to the Antigravity IDE to auto-detect that account."
    };

    private ProviderUsage Error(string message) => new()
    {
        ProviderId = Id,
        DisplayName = DisplayName,
        State = ProviderState.Error,
        ErrorMessage = message,
    };
}
