using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Thin HTTP client for Google's Cloud Code usage backend
/// (<c>cloudcode-pa.googleapis.com</c>) — the same surface the Antigravity IDE and CLI
/// call. Two methods are exposed:
/// <list type="bullet">
/// <item><see cref="RetrieveUserQuotaSummaryAsync"/> — the model groups (Gemini, Claude/GPT)
/// with their weekly + 5-hour quota windows (the gauges).</item>
/// <item><see cref="LoadCodeAssistAsync"/> — subscription tier (the plan label).</item>
/// </list>
/// Both are <c>POST /v1internal:&lt;method&gt;</c> with a JSON body and gzip-compressed
/// responses. Exceptions propagate to <c>GoogleProvider</c>, which converts them to
/// <c>ProviderState.Error</c> rather than throwing.
/// </summary>
internal static class GoogleApiClient
{
    private const string BaseUrl = "https://cloudcode-pa.googleapis.com";
    private const string RetrieveUserQuotaSummaryPath = "/v1internal:retrieveUserQuotaSummary";
    private const string LoadCodeAssistPath = "/v1internal:loadCodeAssist";

    private const string FullEligibilityMode = "FULL_ELIGIBILITY_CHECK";

    // Cockpit identifies itself in the User-Agent; we mirror that so requests look like
    // an in-flight IDE rather than an unknown client.
    private const string UserAgent = "antigravity-cockpit/2.1";

    private static readonly HttpClient HttpClient = new();

    /// <summary>Fetches the per-group, per-window quota summary (weekly + 5-hour).</summary>
    public static async Task<JsonDocument> RetrieveUserQuotaSummaryAsync(
        string accessToken, CancellationToken ct = default)
    {
        // Empty body matches the Antigravity CLI call.
        return await PostAsync(RetrieveUserQuotaSummaryPath, accessToken, "{}", ct);
    }

    /// <summary>Fetches the subscription tier (free / paid) for the plan label.</summary>
    public static async Task<JsonDocument> LoadCodeAssistAsync(
        string accessToken, CancellationToken ct = default)
    {
        string body = JsonSerializer.Serialize(new { mode = FullEligibilityMode });
        return await PostAsync(LoadCodeAssistPath, accessToken, body, ct);
    }

    private static async Task<JsonDocument> PostAsync(
        string path, string accessToken, string jsonBody, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + path)
        {
            Content = new StringContent(jsonBody, null, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        request.Headers.UserAgent.ParseAdd(UserAgent);
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await HttpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct);

        var stream = await response.Content.ReadAsStreamAsync(ct);
        // Decompress if gzipped (Google gzips these responses by default).
        // ContentEncoding is a ICollection<string> of encodings in order.
        if (response.Content.Headers.ContentEncoding.Contains("gzip"))
        {
            stream = new GZipStream(stream, CompressionMode.Decompress);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Drain the (decompressed) error body for a useful message.
            using var reader = new StreamReader(stream);
            string errorBody = await reader.ReadToEndAsync(ct);
            string detail = ParseErrorDetail(errorBody) ??
                            $"{(int)response.StatusCode} {response.ReasonPhrase}";
            throw new InvalidOperationException(
                $"Google Cloud Code API error at {path}: {detail}");
        }

        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static string? ParseErrorDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.Object &&
                err.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.String)
            {
                return msg.GetString();
            }
        }
        catch
        {
            // ignore — fall back to status code text
        }
        return null;
    }
}
