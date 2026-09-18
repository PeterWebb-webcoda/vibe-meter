using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>How a usage fetch ended. The four cases call for four different responses.</summary>
internal enum ClaudeApiOutcome
{
    /// <summary>The endpoint answered with a usage payload.</summary>
    Success,

    /// <summary>
    /// The endpoint refused the credential (401/403). Distinct from a transient failure
    /// because only the user can fix it, and the fix is naming a command to run.
    /// </summary>
    CredentialRejected,

    /// <summary>
    /// The endpoint answered HTTP 429. Distinct from <see cref="Failed"/> because the server
    /// has said, in <c>Retry-After</c>, exactly how long to stay away — and because coming
    /// back sooner is what keeps the limit tripped. See <see cref="ClaudeLiveSourceBackoff"/>.
    /// </summary>
    RateLimited,

    /// <summary>
    /// Anything else — no network, a timeout, a 5xx, an unreadable body. Nothing is wrong
    /// with the sign-in, so the caller falls back to the local files.
    /// </summary>
    Failed,
}

/// <summary>
/// The result of one usage fetch.
/// </summary>
/// <param name="Detail">
/// A short description of a failure, safe to show or log. It never contains the token, a
/// request header, or the response body.
/// </param>
/// <param name="RetryAfter">
/// For <see cref="ClaudeApiOutcome.RateLimited"/>, how long the server asked us to wait,
/// when it said. Null when the header was absent, unreadable or already in the past.
/// </param>
internal sealed record ClaudeApiResult(
    ClaudeApiOutcome Outcome,
    ClaudeUsageData? Data,
    string? Detail,
    TimeSpan? RetryAfter = null)
{
    public static ClaudeApiResult Succeeded(ClaudeUsageData data) => new(ClaudeApiOutcome.Success, data, null);
    public static ClaudeApiResult Rejected(string detail) => new(ClaudeApiOutcome.CredentialRejected, null, detail);
    public static ClaudeApiResult Failed(string detail) => new(ClaudeApiOutcome.Failed, null, detail);

    public static ClaudeApiResult RateLimited(string detail, TimeSpan? retryAfter) =>
        new(ClaudeApiOutcome.RateLimited, null, detail, retryAfter);
}

/// <summary>
/// Fetches Claude subscription usage from Anthropic's OAuth usage endpoint — the same
/// figures <c>/usage</c> shows, asked for directly rather than waiting for some other
/// program to write them to disk.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <c>CodexApiClient</c>, with two deliberate differences.
/// </para>
/// <para>
/// <b>No in-call retry.</b> The Codex client retries transient failures because the API is
/// that provider's only source. Here it is one of three: a failed fetch falls back to the
/// CLI cache and then the desktop history, which is cheaper and quieter than making the
/// user wait through a backoff on every poll. What this client does instead is
/// <i>classify</i> the failure precisely — a 429 with its <c>Retry-After</c> is not the
/// same thing as a 502 — so the provider can decide how long to stay away
/// (<see cref="ClaudeLiveSourceBackoff"/>).
/// </para>
/// <para>
/// <b>A transport seam.</b> The Codex client news up its own <see cref="HttpClient"/>, so
/// its tests cannot reach it. This one accepts a client, so the tests below it stub
/// <see cref="HttpMessageHandler"/> and never touch the network.
/// </para>
/// <para>
/// <b>The endpoint is rate limited per address, at the edge.</b> Observed 2026-09-18 on the
/// Windows machine this was written on: the endpoint answered <c>429 Too Many Requests</c>
/// from Cloudflare (<c>Server: cloudflare</c>, a <c>CF-RAY</c>, and
/// <c>Retry-After: 3505</c>) to every request from this address, with or without a
/// credential and whatever the User-Agent, while <c>/v1/models</c> from the same address
/// answered an ordinary 401. A poll every minute, plus a statusline script that retries on
/// every render once its own cache goes stale, is enough to trip that rule and then keep it
/// tripped; the block lasts about an hour, and whichever caller lands first after it lifts
/// re-trips it. Treating the 429 as an anonymous "Failed" and coming straight back next
/// cycle — which is what this client's first version did, without a word in any log — is
/// precisely the behaviour that sustains the block. Hence the separate outcome, the parsed
/// <c>Retry-After</c>, and a provider that honours it.
/// </para>
/// </remarks>
internal sealed class ClaudeApiClient : IDisposable
{
    internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>The beta header the OAuth usage endpoint requires.</summary>
    private const string OauthBetaValue = "oauth-2025-04-20";

    /// <summary>
    /// The product token is the one the verified call sends — the statusline script that
    /// writes this machine's <c>usage_cache.json</c> with the same token, every few minutes,
    /// successfully — and the comment names what we actually are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The previous value, <c>claude-cli/1.0.0 (external, vibe-meter)</c>, claimed to BE the
    /// CLI, at a version long out of support. It was NOT the cause of the 2026-09-18 failure —
    /// the 429 described in the class remarks was returned whatever the user agent — and it
    /// is changed anyway, for two smaller reasons: it was the one needless difference between
    /// this request and the one proven to work from this machine, and a client should not
    /// announce itself as an obsolete build of a program it is not. The request now differs
    /// from the known-working one in nothing but this comment and the <c>Content-Length: 0</c>
    /// the framework adds for the empty body (see <see cref="BuildRequest"/>).
    /// </para>
    /// <para>
    /// If the endpoint ever does object to a header, <see cref="ClaudeApiResult.Detail"/>
    /// now carries the status code upstream, so the failure names itself instead of vanishing.
    /// </para>
    /// </remarks>
    private const string UserAgentValue = "claude-code/2.0.32 (external, vibe-meter)";

    /// <summary>
    /// Deliberately short. This runs on a UI refresh, and a live source whose whole purpose
    /// is freshness must not hold the card up: past a few seconds the local files are the
    /// better answer.
    /// </summary>
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    /// <summary>Production constructor.</summary>
    public ClaudeApiClient() : this(new HttpClient { Timeout = RequestTimeout }, ownsClient: true) { }

    /// <summary>Testable constructor: the caller supplies the transport and keeps it.</summary>
    internal ClaudeApiClient(HttpClient httpClient, bool ownsClient = false)
    {
        _httpClient = httpClient;
        _ownsClient = ownsClient;
    }

    /// <summary>
    /// Fetches current usage. Never throws: every failure comes back as an outcome, because
    /// the caller's response to one is to fall back rather than to fail.
    /// </summary>
    public async Task<ClaudeApiResult> GetUsageAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ClaudeApiResult.Failed("No access token.");
        }

        try
        {
            using var request = BuildRequest(token);
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // The credential is no longer accepted. Report the status only: the body of
                // a rejection can echo request detail back at us.
                return ClaudeApiResult.Rejected($"the usage endpoint returned HTTP {(int)response.StatusCode}");
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                // The one failure where the server says what to do next. Read the header;
                // report only the status and the wait, never the body.
                var retryAfter = ReadRetryAfter(response);
                var detail = retryAfter is { } wait
                    ? $"the usage endpoint returned HTTP 429 (Retry-After {wait.TotalSeconds:0} s)"
                    : "the usage endpoint returned HTTP 429";
                return ClaudeApiResult.RateLimited(detail, retryAfter);
            }

            if (!response.IsSuccessStatusCode)
            {
                return ClaudeApiResult.Failed($"the usage endpoint returned HTTP {(int)response.StatusCode}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var data = await JsonSerializer
                .DeserializeAsync<ClaudeUsageData>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            // Members the response carries but ClaudeUsageData does not declare —
            // seven_day_opus, seven_day_sonnet, extra_usage, spend and others — are
            // ignored by System.Text.Json, which is correct and must stay that way: the
            // endpoint may add fields at any time and doing so must not break a reading.
            return data is null
                ? ClaudeApiResult.Failed("the usage endpoint returned an empty body")
                : ClaudeApiResult.Succeeded(data);
        }
        catch (Exception ex)
        {
            // The exception's own text is never surfaced. An HttpRequestException can quote
            // the request it failed on, and that request carries the bearer token; a
            // JsonException can quote the body. A fixed description says enough.
            return ClaudeApiResult.Failed(Describe(ex));
        }
    }

    /// <summary>
    /// Reads <c>Retry-After</c> as a wait, whether the server sent seconds or an HTTP-date.
    /// Null when absent, unreadable or already in the past.
    /// </summary>
    internal static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is not { } retryAfter) return null;

        if (retryAfter.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : null;
        }

        if (retryAfter.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : null;
        }

        return null;
    }

    private static HttpRequestMessage BuildRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OauthBetaValue);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd(UserAgentValue);

        // The verified call sends Content-Type: application/json even though a GET carries
        // no body, and .NET only emits a content header when there is content. An empty
        // body keeps the request as close to the known-working one as the framework allows.
        request.Content = new ByteArrayContent([]);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        return request;
    }

    private static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException =>
            $"the usage endpoint did not respond within {RequestTimeout.TotalSeconds:0}s",
        HttpRequestException => "the usage endpoint could not be reached",
        JsonException => "the usage endpoint returned an unreadable response",
        _ => "the usage endpoint could not be read",
    };

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
