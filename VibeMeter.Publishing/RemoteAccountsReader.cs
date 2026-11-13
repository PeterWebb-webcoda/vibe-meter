using System.Net.Http.Headers;
using System.Text.Json;
using VibeMeter.Publishing.AccessToken;

namespace VibeMeter.Publishing;

/// <summary>How one read of the accounts endpoint ended.</summary>
public enum RemoteAccountsReadStatus
{
    /// <summary>200 with a document that parsed.</summary>
    Success,

    /// <summary>204: the API holds nothing for this user. A success — there is simply nothing to show.</summary>
    Empty,

    /// <summary>404: the API in service predates the endpoint. Not worth asking again soon.</summary>
    NotSupported,

    /// <summary>No token, or 401/403.</summary>
    AuthFailure,

    /// <summary>Anything else: 5xx, another 4xx, a network failure, a timeout, or a body that did not parse.</summary>
    Failed,
}

/// <param name="Response">The parsed document; set only for <see cref="RemoteAccountsReadStatus.Success"/>.</param>
public sealed record RemoteAccountsReadResult(
    RemoteAccountsReadStatus Status,
    int? StatusCode,
    RemoteAccountsResponse? Response)
{
    public bool Succeeded => Status is RemoteAccountsReadStatus.Success or RemoteAccountsReadStatus.Empty;
}

/// <summary>Read seam, so the refresher and the host can be tested without a network.</summary>
public interface IRemoteAccountsReader
{
    Task<RemoteAccountsReadResult> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// GETs <c>{ApiBaseUrl}/api/v1/ai-usage/accounts</c> with the same bearer token the
/// publisher uses, resolved against the base URL exactly as
/// <see cref="SnapshotPublisher"/> resolves the snapshots path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never throws</b> (bar the caller's own cancellation): every failure comes back as
/// a <see cref="RemoteAccountsReadResult"/>, because the caller is a detached publish
/// cycle whose only job after publishing is to carry on.
/// </para>
/// <para>
/// <b>One attempt, no retries.</b> This is a nice-to-have display read; retrying and
/// backing off are <see cref="RemoteAccountsRefresher"/>'s job, measured in minutes,
/// not seconds.
/// </para>
/// <para>
/// SECURITY: the token is attached to the request header only and never logged, and
/// response bodies are never surfaced — not even truncated — since they hold another
/// machine's usage, not diagnostics.
/// </para>
/// </remarks>
public sealed class RemoteAccountsReader : IRemoteAccountsReader, IDisposable
{
    internal const string AccountsPath = "api/v1/ai-usage/accounts";

    /// <summary>
    /// Ceiling on the response the client will buffer. The real document is a few
    /// kilobytes; anything near this is not one, and is refused rather than parsed.
    /// </summary>
    private const int MaxResponseBytes = 1024 * 1024;

    private readonly IAccessTokenProvider _tokens;
    private readonly HttpClient _httpClient;

    /// <param name="handler">
    /// The transport, for tests. Null uses the default handler; a supplied one is
    /// disposed with this reader.
    /// </param>
    public RemoteAccountsReader(Uri apiBaseUrl, IAccessTokenProvider tokens, HttpMessageHandler? handler = null)
    {
        _tokens = tokens;
        _httpClient = (handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true));
        _httpClient.BaseAddress = apiBaseUrl;

        // The publisher's per-attempt timeout, so the read can never hold a cycle
        // for longer than one publish attempt already could.
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
        _httpClient.MaxResponseContentBufferSize = MaxResponseBytes;
    }

    public void Dispose() => _httpClient.Dispose();

    public async Task<RemoteAccountsReadResult> ReadAsync(CancellationToken cancellationToken)
    {
        string token;
        try
        {
            token = await _tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Not signed in (or the cached credential has lapsed). The exception text
            // is not carried: this read never prompts, and publishing reports sign-in
            // problems itself.
            return new RemoteAccountsReadResult(RemoteAccountsReadStatus.AuthFailure, null, null);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, AccountsPath);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;

            if (status == 204)
            {
                return new RemoteAccountsReadResult(RemoteAccountsReadStatus.Empty, status, null);
            }

            if (status is 401 or 403)
            {
                return new RemoteAccountsReadResult(RemoteAccountsReadStatus.AuthFailure, status, null);
            }

            if (status == 404)
            {
                return new RemoteAccountsReadResult(RemoteAccountsReadStatus.NotSupported, status, null);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new RemoteAccountsReadResult(RemoteAccountsReadStatus.Failed, status, null);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                // A 200 with nothing in it says the same as a 204.
                return new RemoteAccountsReadResult(RemoteAccountsReadStatus.Empty, status, null);
            }

            var parsed = JsonSerializer.Deserialize<RemoteAccountsResponse>(body, RemoteAccountsJson.Options);
            return parsed is null
                ? new RemoteAccountsReadResult(RemoteAccountsReadStatus.Failed, status, null)
                : new RemoteAccountsReadResult(RemoteAccountsReadStatus.Success, status, parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Network, DNS, TLS, the client timeout, an over-size body, malformed JSON.
            // All the same to the caller: keep what it had and try again later.
            return new RemoteAccountsReadResult(RemoteAccountsReadStatus.Failed, null, null);
        }
    }
}
