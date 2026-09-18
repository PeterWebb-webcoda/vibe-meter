using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>How a usage fetch ended. The three cases call for three different responses.</summary>
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
    /// Anything else — no network, a timeout, a 5xx, an unreadable body. Nothing is wrong
    /// with the sign-in, so the caller simply falls back to the local files and says nothing.
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
internal sealed record ClaudeApiResult(ClaudeApiOutcome Outcome, ClaudeUsageData? Data, string? Detail)
{
    public static ClaudeApiResult Succeeded(ClaudeUsageData data) => new(ClaudeApiOutcome.Success, data, null);
    public static ClaudeApiResult Rejected(string detail) => new(ClaudeApiOutcome.CredentialRejected, null, detail);
    public static ClaudeApiResult Failed(string detail) => new(ClaudeApiOutcome.Failed, null, detail);
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
/// <b>No retry.</b> The Codex client retries transient failures because the API is that
/// provider's only source. Here it is one of three: a failed fetch falls back to the CLI
/// cache and then the desktop history, which is cheaper and quieter than making the user
/// wait through a backoff on every poll.
/// </para>
/// <para>
/// <b>A transport seam.</b> The Codex client news up its own <see cref="HttpClient"/>, so
/// its tests cannot reach it. This one accepts a client, so the tests below it stub
/// <see cref="HttpMessageHandler"/> and never touch the network.
/// </para>
/// </remarks>
internal sealed class ClaudeApiClient : IDisposable
{
    internal const string UsageUrl = "https://api.anthropic.com/api/oauth/usage";

    /// <summary>The beta header the OAuth usage endpoint requires.</summary>
    private const string OauthBetaValue = "oauth-2025-04-20";

    /// <summary>
    /// Identifies us as a Claude Code style client, while naming what we actually are.
    /// Impersonating the CLI outright would be dishonest to the service we are calling.
    /// </summary>
    private const string UserAgentValue = "claude-cli/1.0.0 (external, vibe-meter)";

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
