using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Codex;

/// <summary>
/// Calls the ChatGPT backend wham endpoints that back the Codex usage UI. Transient
/// failures (5xx and 429) are retried with exponential backoff so a momentary backend
/// blip doesn't flip the card to a hard error.
/// </summary>
public sealed class CodexApiClient : IDisposable
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private const int MaxAttempts = 3;
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);

    private readonly HttpClient _httpClient = new();
    private readonly CodexAuth _auth;

    public CodexApiClient(CodexAuth auth) => _auth = auth;

    private Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return Task.FromResult(request);
    }

    public async Task<CodexUsageResponse> GetUsageAsync(string token)
    {
        using var response = await SendWithRetryAsync(HttpMethod.Get, UsageUrl, token);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CodexUsageResponse>(stream)
            ?? throw new InvalidOperationException("Failed to deserialize Codex usage response.");
    }

    public async Task<CodexRateLimitResetResponse> GetRateLimitResetCreditsAsync(string token)
    {
        using var response = await SendWithRetryAsync(HttpMethod.Get, ResetCreditsUrl, token);
        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CodexRateLimitResetResponse>(stream)
            ?? throw new InvalidOperationException("Failed to deserialize Codex rate-limit-reset response.");
    }

    /// <summary>
    /// Sends a request, retrying transient failures (5xx, 429) up to
    /// <see cref="MaxAttempts"/> times with exponential backoff. Honours the
    /// <c>Retry-After</c> header when the server provides it. Throws the final
    /// non-success status as an <see cref="HttpRequestException"/> if all retries fail.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method, string url, string token)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = await CreateRequestAsync(method, url, token);
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode || !IsTransient(response.StatusCode) || attempt >= MaxAttempts)
            {
                if (!response.IsSuccessStatusCode)
                {
                    response.EnsureSuccessStatusCode();
                }
                return response;
            }

            // Transient failure — back off and retry. Dispose the failed response.
            var delay = GetDelay(response, attempt);
            response.Dispose();
            await Task.Delay(delay);
        }
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        (int)statusCode is 429 or >= 500 and <= 599;

    private static TimeSpan GetDelay(HttpResponseMessage response, int attempt)
    {
        // Honour Retry-After when present (seconds or HTTP-date); otherwise exponential backoff.
        if (response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is { } retryDelta) return ClampDelay(retryDelta);
            if (retryAfter.Date is { } date)
            {
                var dateDelta = date - DateTimeOffset.UtcNow;
                if (dateDelta > TimeSpan.Zero) return ClampDelay(dateDelta);
            }
        }
        return ClampDelay(TimeSpan.FromTicks(InitialBackoff.Ticks * (1L << (attempt - 1))));
    }

    /// <summary>Keeps backoff sane — never longer than 8s, even if Retry-After asks for more.</summary>
    private static TimeSpan ClampDelay(TimeSpan d) =>
        d < TimeSpan.Zero ? TimeSpan.Zero
        : d > TimeSpan.FromSeconds(8) ? TimeSpan.FromSeconds(8)
        : d;

    public void Dispose() => _httpClient.Dispose();
}
