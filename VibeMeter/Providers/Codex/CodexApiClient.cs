using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Codex;

/// <summary>
/// Calls the ChatGPT backend wham endpoints that back the Codex usage UI.
/// </summary>
public sealed class CodexApiClient : IDisposable
{
    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const string ResetCreditsUrl = "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits";

    private readonly HttpClient _httpClient = new();
    private readonly CodexAuth _auth;

    public CodexApiClient(CodexAuth auth) => _auth = auth;

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<CodexUsageResponse> GetUsageAsync(string token)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, UsageUrl, token);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CodexUsageResponse>(stream)
            ?? throw new InvalidOperationException("Failed to deserialize Codex usage response.");
    }

    public async Task<CodexRateLimitResetResponse> GetRateLimitResetCreditsAsync(string token)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, ResetCreditsUrl, token);
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        return await JsonSerializer.DeserializeAsync<CodexRateLimitResetResponse>(stream)
            ?? throw new InvalidOperationException("Failed to deserialize Codex rate-limit-reset response.");
    }

    public void Dispose() => _httpClient.Dispose();
}
