using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using VibeMeter.Agent.AccessToken;

namespace VibeMeter.Agent.Publishing;

/// <summary>Terminal result of one publish (retries already exhausted internally).</summary>
public enum PublishOutcome
{
    /// <summary>The API accepted the snapshot.</summary>
    Success,

    /// <summary>401/403 or no token — a credentials problem, surfaced distinctly.</summary>
    AuthFailure,

    /// <summary>Other 4xx — the request itself is wrong; retrying can never succeed.</summary>
    PermanentFailure,

    /// <summary>5xx or network failure — worth queueing and retrying later.</summary>
    TransientFailure,
}

public sealed record PublishResult(PublishOutcome Outcome, int? StatusCode, string? Detail)
{
    public bool Succeeded => Outcome == PublishOutcome.Success;
}

public static class IdempotencyKey
{
    /// <summary>
    /// Derives the Idempotency-Key header from the exact document bytes:
    /// "v1-" + SHA-256 as lowercase hex (67 characters — inside the API's
    /// 128-character limit, and pure hex satisfies its
    /// <c>^[A-Za-z0-9][A-Za-z0-9._:-]*$</c> pattern). Because mapping is
    /// deterministic, a retry of the same snapshot always reuses the same key,
    /// so the server de-duplicates instead of storing a second row.
    /// </summary>
    public static string For(string document) =>
        "v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))).ToLowerInvariant();
}

/// <summary>
/// POSTs snapshot documents to <c>{ApiBaseUrl}/api/v1/ai-usage/snapshots</c>.
/// SECURITY: the bearer token is read per attempt from
/// <see cref="IAccessTokenProvider"/> and attached only to the request header —
/// it must never be logged, and no diagnostic ever includes request headers.
/// Response bodies are truncated before surfacing (they are our own API's
/// validation messages; provider responses never reach this class).
/// </summary>
public sealed class SnapshotPublisher : IDisposable
{
    private const string SnapshotsPath = "api/v1/ai-usage/snapshots";
    private const int MaxAttempts = 3;
    private const int MaxDetailLength = 300;
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    private readonly IAccessTokenProvider _tokens;
    private readonly HttpClient _httpClient;

    public SnapshotPublisher(Uri apiBaseUrl, IAccessTokenProvider tokens)
    {
        _tokens = tokens;
        _httpClient = new HttpClient
        {
            BaseAddress = apiBaseUrl,
            // Per attempt; combined with the retry cap below this bounds a cycle.
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public void Dispose() => _httpClient.Dispose();

    /// <summary>
    /// Publishes one document. Transient failures (5xx, network) are retried in
    /// place with jittered backoff; auth failures and other 4xx are returned
    /// immediately so the caller can queue-or-drop.
    /// </summary>
    public async Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            string token;
            try
            {
                token = await _tokens.GetAccessTokenAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A missing/invalid token is an authentication problem, not a
                // network one. The message names the env var — safe to surface.
                return new PublishResult(PublishOutcome.AuthFailure, null, ex.Message);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, SnapshotsPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Add("Idempotency-Key", IdempotencyKey.For(document));
            request.Content = new StringContent(document, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // Network, DNS, TLS, or the 30 s client timeout — all transient.
                if (attempt >= MaxAttempts)
                {
                    return new PublishResult(PublishOutcome.TransientFailure, null, ex.Message);
                }

                await DelayBeforeRetryAsync(attempt, cancellationToken);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    return new PublishResult(PublishOutcome.Success, (int)response.StatusCode, null);
                }

                var status = (int)response.StatusCode;
                var detail = await ReadDetailAsync(response, cancellationToken);

                if (status is 401 or 403)
                {
                    return new PublishResult(PublishOutcome.AuthFailure, status, detail);
                }

                // Other 4xx: the request is wrong (validation, too-old snapshot,
                // payload limits) and will fail identically forever — never retry.
                if (status < 500)
                {
                    return new PublishResult(PublishOutcome.PermanentFailure, status, detail);
                }

                if (attempt >= MaxAttempts)
                {
                    return new PublishResult(PublishOutcome.TransientFailure, status, detail);
                }

                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }
    }

    private static async Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseDelay = BackoffDelays[Math.Min(attempt, BackoffDelays.Length) - 1];

        // ±50% jitter so several agents restarting together don't hammer the API in lock-step.
        var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * (0.5 + Random.Shared.NextDouble()));
        await Task.Delay(delay, cancellationToken);
    }

    private static async Task<string> ReadDetailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = (await response.Content.ReadAsStringAsync(cancellationToken)).Trim();
            if (body.Length == 0)
            {
                return "no body";
            }

            return body.Length <= MaxDetailLength ? body : body[..MaxDetailLength] + "...";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return "unreadable body";
        }
    }
}
