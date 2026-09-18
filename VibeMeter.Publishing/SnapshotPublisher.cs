using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using VibeMeter.Publishing.AccessToken;

namespace VibeMeter.Publishing;

/// <summary>Terminal result of one publish (retries already exhausted internally).</summary>
public enum PublishOutcome
{
    /// <summary>The API accepted the snapshot.</summary>
    Success,

    /// <summary>401/403 or no token — a credentials problem, surfaced distinctly.</summary>
    AuthFailure,

    /// <summary>
    /// A 4xx other than 401/403/429 — this build of the API refused the request
    /// itself, so repeating it in place cannot help. It does NOT mean the
    /// document is worthless: the API on the other end can change under us (a
    /// rollback leaves an older validator rejecting a newer payload), so the
    /// caller retains the snapshot for a bounded number of later attempts
    /// rather than destroying it. See <see cref="SnapshotPublishCycle.FlushQueueAsync"/>.
    /// </summary>
    PermanentFailure,

    /// <summary>
    /// 5xx, 429, or a network failure — the same request can succeed later, so
    /// it is worth retrying in place and then queueing.
    /// </summary>
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
/// Publish seam for the host loop, so a test can run a cycle and observe what
/// would (or would not) go onto the wire without any network involved.
/// </summary>
public interface ISnapshotPublisher
{
    Task<PublishResult> PublishAsync(string document, CancellationToken cancellationToken);
}

/// <summary>
/// POSTs snapshot documents to <c>{ApiBaseUrl}/api/v1/ai-usage/snapshots</c>.
/// SECURITY: the bearer token is read per attempt from
/// <see cref="IAccessTokenProvider"/> and attached only to the request header —
/// it must never be logged, and no diagnostic ever includes request headers.
/// Response bodies are truncated before surfacing (they are our own API's
/// validation messages; provider responses never reach this class).
/// </summary>
public sealed class SnapshotPublisher : ISnapshotPublisher, IDisposable
{
    private const string SnapshotsPath = "api/v1/ai-usage/snapshots";
    private const int MaxAttempts = 3;
    private const int MaxDetailLength = 300;
    private static readonly TimeSpan[] BackoffDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
    ];

    /// <summary>
    /// Ceiling on a <c>Retry-After</c> the server asks us to honour. A rate
    /// limiter is entitled to say "come back in an hour", and a hostile or
    /// simply mistaken value could say far worse; obeying either literally
    /// would wedge the collection loop — and its shutdown — behind one header.
    /// Past this the agent stops waiting, exhausts its attempts and lets the
    /// cycle queue the snapshot instead, which costs latency, not data.
    /// Matched to the per-attempt HTTP timeout so one attempt can never block
    /// the cycle for longer than the transport already could.
    /// </summary>
    private static readonly TimeSpan MaxRetryAfterDelay = TimeSpan.FromSeconds(30);

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
    /// Publishes one document. Transient failures (5xx, 429, network) are
    /// retried in place with jittered backoff, honouring any
    /// <c>Retry-After</c>; auth failures and the remaining 4xx are returned
    /// immediately so the caller can decide how long to keep the document.
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

                // No response, so no Retry-After to honour.
                await DelayBeforeRetryAsync(attempt, retryAfter: null, cancellationToken);
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

                // 429 is the one 4xx that says nothing about the request: the
                // server is asking us to slow down, so the identical payload
                // succeeds once the window moves. Classing it permanent (as
                // "any 4xx is permanent" used to) meant a rate-limited agent
                // never retried at all.
                if (status is not 429 && status < 500)
                {
                    // The remaining 4xx: this build of the API refused the
                    // request (validation, too-old snapshot, payload limits).
                    // Repeating it immediately cannot help, so stop here — but
                    // see PermanentFailure: the caller still keeps the document.
                    return new PublishResult(PublishOutcome.PermanentFailure, status, detail);
                }

                if (attempt >= MaxAttempts)
                {
                    return new PublishResult(PublishOutcome.TransientFailure, status, detail);
                }

                await DelayBeforeRetryAsync(attempt, response.Headers.RetryAfter, cancellationToken);
            }
        }
    }

    private static async Task DelayBeforeRetryAsync(
        int attempt,
        RetryConditionHeaderValue? retryAfter,
        CancellationToken cancellationToken)
    {
        var baseDelay = BackoffDelays[Math.Min(attempt, BackoffDelays.Length) - 1];

        // ±50% jitter so several agents restarting together don't hammer the API in lock-step.
        var delay = TimeSpan.FromMilliseconds(baseDelay.TotalMilliseconds * (0.5 + Random.Shared.NextDouble()));

        // Retry-After is a floor, not a replacement: when the server names a
        // longer wait we take it, and when it names a shorter one we still
        // keep our own backoff rather than hammering it sooner.
        var requested = RequestedRetryDelay(retryAfter, DateTimeOffset.UtcNow);
        await Task.Delay(requested > delay ? requested : delay, cancellationToken);
    }

    /// <summary>
    /// The wait a <c>Retry-After</c> header asks for, clamped into
    /// [0, <see cref="MaxRetryAfterDelay"/>]. Both wire forms are honoured:
    /// delta-seconds, and an HTTP-date turned into a delta against
    /// <paramref name="utcNow"/>. Returns <see cref="TimeSpan.Zero"/> when the
    /// header is absent, unparseable (HttpClient leaves such a header
    /// unparsed rather than throwing), or names a moment already past — in
    /// each case the caller falls back to its own backoff.
    /// </summary>
    internal static TimeSpan RequestedRetryDelay(RetryConditionHeaderValue? retryAfter, DateTimeOffset utcNow)
    {
        var requested = retryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - utcNow,
            _ => TimeSpan.Zero,
        };

        if (requested <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return requested > MaxRetryAfterDelay ? MaxRetryAfterDelay : requested;
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
