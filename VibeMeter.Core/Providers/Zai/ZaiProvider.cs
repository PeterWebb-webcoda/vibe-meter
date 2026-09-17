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
            
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ProviderUsage
                {
                    ProviderId = Id,
                    DisplayName = DisplayName,
                    State = ProviderState.Error,
                    ErrorMessage = "Unexpected API response shape (root is not an object)."
                };
            }

            // The quota endpoint can answer HTTP 200 with an API-level failure carried in
            // the JSON envelope, e.g. { "code": 500, "msg": "Internal service error",
            // "success": false, "data": null }. Inspect the envelope before touching 'data'.
            int? apiCode = TryGetInt(root, "code", out var codeValue) ? codeValue : null;
            string? apiMessage = GetStringOrNull(root, "msg") ?? GetStringOrNull(root, "message");
            bool apiFailed =
                (root.TryGetProperty("success", out var successProp) && successProp.ValueKind == JsonValueKind.False)
                || (apiCode is int codeNumber && codeNumber != 200 && codeNumber != 0);

            if (apiFailed)
            {
                return new ProviderUsage
                {
                    ProviderId = Id,
                    DisplayName = DisplayName,
                    State = ProviderState.Error,
                    ErrorMessage = apiCode is int failedCode
                        ? $"Z.ai API error {failedCode}: {apiMessage ?? "unknown error"}"
                        : $"Z.ai API error: {apiMessage ?? "unknown error"}"
                };
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind == JsonValueKind.Null)
            {
                return new ProviderUsage
                {
                    ProviderId = Id,
                    DisplayName = DisplayName,
                    State = ProviderState.Error,
                    ErrorMessage = "Unexpected API response shape (missing or null 'data')."
                };
            }

            if (data.ValueKind != JsonValueKind.Object)
            {
                return new ProviderUsage
                {
                    ProviderId = Id,
                    DisplayName = DisplayName,
                    State = ProviderState.Error,
                    ErrorMessage = "Unexpected API response shape ('data' is not an object)."
                };
            }

            string? level = GetStringOrNull(data, "level");
            if (string.IsNullOrEmpty(level))
            {
                level = "unknown";
            }

            string planLabel = level == "unknown" 
                ? "GLM Coding Plan" 
                : $"GLM Coding — {char.ToUpper(level[0])}{level[1..]}";

            var gauges = new List<UsageGauge>();
            if (data.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
            {
                foreach (var limit in limits.EnumerateArray())
                {
                    if (limit.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    string type = GetStringOrNull(limit, "type") ?? "";
                    int unit = TryGetInt(limit, "unit", out var unitValue) ? unitValue : 0;
                    int usedPct = TryGetInt(limit, "percentage", out var usedPctValue) ? usedPctValue : 0;
                    long resetMs = TryGetInt64(limit, "nextResetTime", out var resetMsValue) ? resetMsValue : 0;

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

            // Ensure gauges are ordered consistently: 5h first, weekly second, monthly third.
            gauges.Sort((a, b) =>
            {
                var order = new Dictionary<string, int>
                {
                    { "5h", 1 },
                    { "weekly", 2 },
                    { "monthly", 3 }
                };
                int aOrd = order.TryGetValue(a.Id, out var ao) ? ao : 99;
                int bOrd = order.TryGetValue(b.Id, out var bo) ? bo : 99;
                return aOrd.CompareTo(bOrd);
            });

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

    /// <summary>Reads a JSON property as a string, or null when absent or not a string.</summary>
    private static string? GetStringOrNull(JsonElement element, string name) =>
        element.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    /// <summary>Reads a JSON property as an int; false when absent or not an integer number.</summary>
    private static bool TryGetInt(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt32(out value);
    }

    /// <summary>Reads a JSON property as a long; false when absent or not an integer number.</summary>
    private static bool TryGetInt64(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var prop)
            && prop.ValueKind == JsonValueKind.Number
            && prop.TryGetInt64(out value);
    }
}
