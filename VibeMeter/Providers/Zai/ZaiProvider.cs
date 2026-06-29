using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using VibeMeter.Core;

namespace VibeMeter.Providers.Zai;

/// <summary>
/// Z.ai GLM coding subscription provider.
/// </summary>
public sealed class ZaiProvider : IUsageProvider
{
    public string Id => "zai";
    public string DisplayName => "Z.ai GLM";

    private readonly ZaiAuth _auth;
    private static readonly HttpClient _httpClient = new();

    /// <summary>Production constructor.</summary>
    public ZaiProvider() : this(new ZaiAuth()) { }

    /// <summary>Testable constructor.</summary>
    public ZaiProvider(ZaiAuth auth) => _auth = auth;

    public async Task<ProviderUsage> FetchAsync()
    {
        if (!_auth.IsConfigured)
        {
            return new ProviderUsage
            {
                ProviderId = Id,
                DisplayName = DisplayName,
                State = ProviderState.NotConfigured,
                ErrorMessage =
                    "Set the ZAI_API_KEY env var (or install a supported GLM coding CLI) " +
                    "to enable Z.ai."
            };
        }

        var apiKey = _auth.GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new ProviderUsage
            {
                ProviderId = Id,
                DisplayName = DisplayName,
                State = ProviderState.NotConfigured,
                ErrorMessage =
                    $"Z.ai is configured ({_auth.DetectionLabel}), but no API key was found in env vars. " +
                    "Set the ZAI_API_KEY env var to view live quota."
            };
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.z.ai/api/monitor/usage/quota/limit");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request);
            
            if (!response.IsSuccessStatusCode)
            {
                return new ProviderUsage
                {
                    ProviderId = Id,
                    DisplayName = DisplayName,
                    State = ProviderState.Error,
                    ErrorMessage = $"Z.ai API error: {(int)response.StatusCode} {response.ReasonPhrase}"
                };
            }

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            
            if (!json.RootElement.TryGetProperty("data", out var data))
            {
                return new ProviderUsage
                {
                    ProviderId = Id,
                    DisplayName = DisplayName,
                    State = ProviderState.Error,
                    ErrorMessage = "Unexpected API response shape (missing 'data')."
                };
            }

            string level = "unknown";
            if (data.TryGetProperty("level", out var levelProp) && levelProp.ValueKind == JsonValueKind.String)
            {
                level = levelProp.GetString() ?? "unknown";
            }

            string planLabel = level == "unknown" 
                ? "GLM Coding Plan" 
                : $"GLM Coding — {char.ToUpper(level[0])}{level[1..]}";

            var gauges = new List<UsageGauge>();
            if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limits.EnumerateArray())
                {
                    string type = limit.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "";
                    int unit = limit.TryGetProperty("unit", out var uProp) ? uProp.GetInt32() : 0;
                    int usedPct = limit.TryGetProperty("percentage", out var pProp) ? pProp.GetInt32() : 0;
                    long resetMs = limit.TryGetProperty("nextResetTime", out var rProp) ? rProp.GetInt64() : 0;

                    var (id, title) = (type, unit) switch
                    {
                        ("TOKENS_LIMIT", 3) => ("5h",     "5-Hour Quota"),
                        ("TOKENS_LIMIT", 6) => ("weekly", "Weekly Quota"),
                        ("TIME_LIMIT",   5) => ("monthly", "Monthly (Search/Tools)"),
                        _                   => ($"{type}_{unit}".ToLowerInvariant(), $"{type} (unit {unit})")
                    };

                    gauges.Add(new UsageGauge(
                        Id: id,
                        Title: title,
                        Subtitle: null,
                        PercentRemaining: Math.Max(0, 100 - usedPct),
                        ResetAt: resetMs > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(resetMs).LocalDateTime : null
                    ));
                }
            }

            return new ProviderUsage
            {
                ProviderId = Id,
                DisplayName = DisplayName,
                State = ProviderState.Ok,
                PlanLabel = planLabel,
                Gauges = gauges
            };
        }
        catch (Exception ex)
        {
            return new ProviderUsage
            {
                ProviderId = Id,
                DisplayName = DisplayName,
                State = ProviderState.Error,
                ErrorMessage = $"Failed to fetch Z.ai quota: {ex.Message}"
            };
        }
    }
}
