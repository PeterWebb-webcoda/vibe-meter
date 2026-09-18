using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using VibeMeter.Agent.AccessToken;
using VibeMeter.Agent.Publishing;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// SnapshotPublisher's status handling: what is retried, what is not, and what
/// the terminal outcome says. The publisher news up its own HttpClient (there
/// is no HttpMessageHandler seam to inject), so the transport is exercised for
/// real — against a scripted HTTP/1.1 stub listening on 127.0.0.1. Nothing ever
/// leaves the machine. The hard-coded jittered backoff makes the retry tests
/// real-time: they take a few seconds each by design.
/// </summary>
public sealed class SnapshotPublisherTests
{
    // Mirrors AiUsageRequestValidator.MaximumIdempotencyKeyLength.
    private const int MaximumKeyLength = 128;

    private const string BearerToken = "stub-agent-bearer-token-0123456789abcdef-DO-NOT-LOG";
    private const string Document = """{"providers":[{"providerId":"codex"}]}""";
    private const string SnapshotsPath = "/api/v1/ai-usage/snapshots";

    // The worst scripted cycle is two backoffs (≈3 s + ≈6 s at most, and no
    // scripted Retry-After here asks for longer) plus three attempts; past
    // this a test is wedged, not slow, and should fail.
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(25);

    // --------------------------------------------- the required behaviours

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    [InlineData(202)]
    [InlineData(204)]
    public async Task AnySuccessStatus_IsReportedAsSuccess(int status)
    {
        using var api = LoopbackApi.Serving(status);

        var result = await PublishOnceAsync(api);

        Assert.Equal(PublishOutcome.Success, result.Outcome);
        Assert.Equal(status, result.StatusCode);
        var request = Assert.Single(api.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(SnapshotsPath, request.Path);
        Assert.Empty(api.Faults);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task AuthRejections_AreAuthFailures_DistinctAndNeverRetried(int status)
    {
        // Exactly one scripted response: any retry would get a 500, flip the
        // outcome to TransientFailure, and fail the assertions below twice over.
        using var api = LoopbackApi.Serving(status);

        var result = await PublishOnceAsync(api);

        Assert.Equal(PublishOutcome.AuthFailure, result.Outcome);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(1, api.RequestCount);
        Assert.Equal("stub-unauthorized-or-forbidden-detail", result.Detail);
        Assert.Empty(api.Faults);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(422)]
    public async Task OtherClientErrors_ArePermanentFailures_NeverRetried(int status)
    {
        using var api = LoopbackApi.Serving(status);

        var result = await PublishOnceAsync(api);

        Assert.Equal(PublishOutcome.PermanentFailure, result.Outcome);
        Assert.Equal(status, result.StatusCode);
        Assert.Equal(1, api.RequestCount);
        Assert.Empty(api.Faults);
    }

    [Fact]
    public async Task RateLimited_IsRetried_AfterWaitingOutTheRetryAfterHeader()
    {
        // The regression: 429 is below 500, so the old "every other 4xx is
        // permanent" branch returned before the retry — a rate-limited agent
        // never tried again, and the host then threw the snapshot away.
        using var api = LoopbackApi.Serving(
            Scripted(429, retryAfter: "5"),
            Scripted(200));

        var elapsed = Stopwatch.StartNew();
        var result = await PublishOnceAsync(api);
        elapsed.Stop();

        Assert.Equal(PublishOutcome.Success, result.Outcome);
        Assert.Equal(2, api.RequestCount);

        // The jittered backoff for the first retry tops out at 3 s, so a wait
        // anywhere near 5 s can only be the Retry-After being honoured. The
        // small tolerance is for the timer firing a tick early.
        Assert.True(
            elapsed.Elapsed >= TimeSpan.FromSeconds(4.5),
            $"retried after only {elapsed.Elapsed}, so Retry-After was ignored");

        // Still one key across the retry, so the server de-duplicates.
        Assert.Single(api.Requests.Select(request => request.IdempotencyKey).Distinct());
        Assert.Empty(api.Faults);
    }

    [Fact]
    public async Task RateLimitedThroughout_IsTransient_NotPermanent()
    {
        // Retried to the cap and then reported transient, which is what makes
        // the host queue the snapshot instead of discarding it.
        using var api = LoopbackApi.Serving(
            Scripted(429, retryAfter: "1"),
            Scripted(429, retryAfter: "1"),
            Scripted(429, retryAfter: "1"));

        var result = await PublishOnceAsync(api);

        Assert.Equal(PublishOutcome.TransientFailure, result.Outcome);
        Assert.Equal(429, result.StatusCode);
        Assert.Equal(3, api.RequestCount);
        Assert.Empty(api.Faults);
    }

    [Fact]
    public void RetryAfter_HonoursBothWireForms_AndIsBounded()
    {
        var now = new DateTimeOffset(2026, 9, 18, 4, 0, 0, TimeSpan.Zero);

        // No header at all: the caller keeps its own backoff.
        Assert.Equal(TimeSpan.Zero, SnapshotPublisher.RequestedRetryDelay(null, now));

        // delta-seconds and HTTP-date are both legal spellings of the same ask.
        Assert.Equal(
            TimeSpan.FromSeconds(7),
            SnapshotPublisher.RequestedRetryDelay(new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)), now));
        Assert.Equal(
            TimeSpan.FromSeconds(12),
            SnapshotPublisher.RequestedRetryDelay(new RetryConditionHeaderValue(now.AddSeconds(12)), now));

        // A date already past asks for no extra wait, never a negative one.
        Assert.Equal(
            TimeSpan.Zero,
            SnapshotPublisher.RequestedRetryDelay(new RetryConditionHeaderValue(now.AddMinutes(-5)), now));

        // Hostile or merely absurd values are clamped: one response header must
        // never be able to stall the agent (or its shutdown) for hours.
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            SnapshotPublisher.RequestedRetryDelay(new RetryConditionHeaderValue(TimeSpan.FromDays(3)), now));
        Assert.Equal(
            TimeSpan.FromSeconds(30),
            SnapshotPublisher.RequestedRetryDelay(new RetryConditionHeaderValue(now.AddYears(1)), now));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public async Task ServerErrors_AreRetriedToTheCap_ThenReportedTransient(int status)
    {
        using var api = LoopbackApi.Serving(status, status, status);

        var result = await PublishOnceAsync(api);

        Assert.Equal(PublishOutcome.TransientFailure, result.Outcome);
        Assert.Equal(status, result.StatusCode);
        // The implemented policy caps at three attempts: neither one-and-done
        // nor unbounded.
        Assert.Equal(3, api.RequestCount);
        Assert.Equal("stub-server-error", result.Detail);
        Assert.DoesNotContain(BearerToken, result.Detail);
        Assert.Empty(api.Faults);
    }

    [Fact]
    public async Task ConnectionRefused_IsRetriedToTheCap_ThenReportedTransient()
    {
        // A loopback port with nothing listening reproduces the
        // HttpRequestException shape of a dead network — no stub can fake that
        // path more honestly.
        var tokens = new StubTokens();
        using var publisher = new SnapshotPublisher(UnreachableBaseAddress(), tokens);

        var result = await publisher
            .PublishAsync(Document, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Equal(PublishOutcome.TransientFailure, result.Outcome);
        Assert.Null(result.StatusCode);
        // One token fetch per attempt: proof the policy actually retried.
        Assert.Equal(3, tokens.Calls);
        Assert.DoesNotContain(BearerToken, result.Detail);
    }

    [Fact]
    public async Task ServerErrorsThenSuccess_RetriesWithOneIdempotencyKey()
    {
        // This is the duplicate-row guard: the retry of the same payload must
        // carry the very same key so the server de-duplicates instead of
        // storing a second row.
        using var api = LoopbackApi.Serving(500, 500, 200);

        var result = await PublishOnceAsync(api);

        Assert.Equal(PublishOutcome.Success, result.Outcome);
        Assert.Equal(3, api.RequestCount);

        var keys = api.Requests.Select(r => r.IdempotencyKey).ToArray();
        Assert.Single(keys.Distinct());
        Assert.All(keys, key => Assert.InRange(key.Length, 1, MaximumKeyLength));

        var bodies = api.Requests.Select(r => r.Body).ToArray();
        Assert.Single(bodies.Distinct());

        Assert.All(
            api.Requests,
            request => Assert.Equal($"Bearer {BearerToken}", request.Authorization));
        Assert.Empty(api.Faults);
    }

    [Fact]
    public async Task EveryRequest_CarriesTheDocumentedIdempotencyKey()
    {
        using var api = LoopbackApi.Serving(200);

        await PublishOnceAsync(api);

        var request = Assert.Single(api.Requests);
        Assert.Equal(IdempotencyKey.For(Document), request.IdempotencyKey);
        Assert.InRange(request.IdempotencyKey.Length, 1, MaximumKeyLength);
    }

    [Fact]
    public async Task MissingToken_IsAuthFailure_BeforeAnythingHitsTheWire()
    {
        using var api = LoopbackApi.Serving(200); // would succeed if wrongly sent
        var tokens = new StubTokens { Fault = "no token configured (stub)" };
        using var publisher = new SnapshotPublisher(api.BaseAddress, tokens);

        var result = await publisher
            .PublishAsync(Document, CancellationToken.None)
            .WaitAsync(TestTimeout);

        Assert.Equal(PublishOutcome.AuthFailure, result.Outcome);
        Assert.Null(result.StatusCode);
        Assert.Equal(0, api.RequestCount);
        Assert.DoesNotContain(BearerToken, result.Detail);
    }

    [Fact]
    public async Task NoOutcome_EverSurfacesTheBearerToken()
    {
        // The publisher's only writable surfaces are the result detail (the
        // response body) and the exception message on the token path. Whatever
        // a future diagnostic adds must never include the request headers, so
        // the token must not appear in either.
        var written = new List<string?>();

        foreach (var status in new[] { 200, 401, 403, 400, 422 })
        {
            using var api = LoopbackApi.Serving(status);
            var result = await PublishOnceAsync(api);
            Assert.Empty(api.Faults);
            written.Add(result.Detail);
            written.Add(result.ToString());
        }

        using var noTokenApi = LoopbackApi.Serving(200);
        var missingToken = new StubTokens { Fault = "no token configured (stub)" };
        using var publisher = new SnapshotPublisher(noTokenApi.BaseAddress, missingToken);
        var tokenFailure = await publisher
            .PublishAsync(Document, CancellationToken.None)
            .WaitAsync(TestTimeout);
        Assert.Empty(noTokenApi.Faults);
        written.Add(tokenFailure.Detail);
        written.Add(tokenFailure.ToString());

        Assert.All(written, text => Assert.DoesNotContain(BearerToken, text));
    }

    // --------------------------------------------- harness

    private static async Task<PublishResult> PublishOnceAsync(LoopbackApi api)
    {
        using var publisher = new SnapshotPublisher(api.BaseAddress, new StubTokens());
        return await publisher
            .PublishAsync(Document, CancellationToken.None)
            .WaitAsync(TestTimeout);
    }

    /// <summary>A loopback port that is bound then released: every connect to
    /// it is refused, which is exactly what a dead network produces.</summary>
    private static Uri UnreachableBaseAddress()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new Uri($"http://127.0.0.1:{port}/");
    }

    private sealed class StubTokens : IAccessTokenProvider
    {
        public string? Fault;
        public int Calls;

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Fault is not null
                ? Task.FromException<string>(new InvalidOperationException(Fault))
                : Task.FromResult(BearerToken);
        }
    }

    private sealed record CapturedRequest(
        string Method,
        string Path,
        string? Authorization,
        string IdempotencyKey,
        string Body);

    /// <summary>One scripted response: a status, and optionally the exact
    /// <c>Retry-After</c> value to send with it (raw, so both the
    /// delta-seconds and HTTP-date spellings can be exercised on the wire).</summary>
    private sealed record ScriptedResponse(int Status, string? RetryAfter = null);

    private static ScriptedResponse Scripted(int status, string? retryAfter = null) =>
        new(status, retryAfter);

    /// <summary>A scripted HTTP/1.1 stub on 127.0.0.1. Each request consumes
    /// the next scripted status code; once the script runs dry it serves 500,
    /// so an unexpected retry flips the outcome and fails the calling test. It
    /// speaks just enough HTTP for the single POST the publisher sends.</summary>
    private sealed class LoopbackApi : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _shutdown = new();
        private readonly ConcurrentQueue<CapturedRequest> _requests = new();
        private readonly ConcurrentQueue<ScriptedResponse> _script;
        private Task _acceptLoop = Task.CompletedTask;

        private LoopbackApi(ScriptedResponse[] script)
        {
            _script = new ConcurrentQueue<ScriptedResponse>(script);
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        }

        public static LoopbackApi Serving(params int[] script) =>
            Serving(script.Select(status => new ScriptedResponse(status)).ToArray());

        public static LoopbackApi Serving(params ScriptedResponse[] script)
        {
            var api = new LoopbackApi(script);
            api._acceptLoop = Task.Run(api.AcceptLoopAsync);
            return api;
        }

        public Uri BaseAddress { get; }

        public int RequestCount => _requests.Count;

        public IReadOnlyList<CapturedRequest> Requests => _requests.ToArray();

        public ConcurrentQueue<Exception> Faults { get; } = new();

        public void Dispose()
        {
            _shutdown.Cancel();
            try
            {
                _listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped — nothing to clean up.
            }

            try
            {
                _acceptLoop.Wait(TimeSpan.FromSeconds(1));
            }
            catch (Exception)
            {
                // Shutdown races the accept loop — harmless.
            }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException
                or ObjectDisposedException or SocketException)
            {
                // Expected during shutdown or listener teardown.
            }
            catch (Exception ex)
            {
                Faults.Enqueue(ex);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                while (!_shutdown.IsCancellationRequested)
                {
                    var request = await ReadRequestAsync(stream);
                    if (request is null)
                    {
                        return; // the client closed the connection
                    }

                    _requests.Enqueue(request);
                    await WriteResponseAsync(
                        stream,
                        _script.TryDequeue(out var scripted) ? scripted : new ScriptedResponse(500));
                }
            }
        }

        private async Task<CapturedRequest?> ReadRequestAsync(NetworkStream stream)
        {
            var data = new MemoryStream();
            var buffer = new byte[8192];
            int headerEnd;

            while ((headerEnd = IndexOfHeaderEnd(data)) < 0)
            {
                var read = await stream.ReadAsync(buffer, _shutdown.Token);
                if (read == 0)
                {
                    return null;
                }

                data.Write(buffer, 0, read);
            }

            var bytes = data.ToArray();
            var lines = Encoding.ASCII.GetString(bytes, 0, headerEnd).Split("\r\n");
            var requestLine = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                var colon = line.IndexOf(':');
                if (colon > 0)
                {
                    headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
                }
            }

            var contentLength = headers.TryGetValue("Content-Length", out var length)
                ? int.Parse(length, CultureInfo.InvariantCulture)
                : 0;
            var total = headerEnd + 4 + contentLength;
            while (bytes.Length < total)
            {
                var read = await stream.ReadAsync(buffer, _shutdown.Token);
                if (read == 0)
                {
                    throw new IOException("client closed before the body completed");
                }

                data.Write(buffer, 0, read);
                bytes = data.ToArray();
            }

            return new CapturedRequest(
                requestLine[0],
                requestLine[1],
                headers.GetValueOrDefault("Authorization"),
                headers.GetValueOrDefault("Idempotency-Key") ?? string.Empty,
                Encoding.UTF8.GetString(bytes, headerEnd + 4, contentLength));
        }

        private async Task WriteResponseAsync(NetworkStream stream, ScriptedResponse scripted)
        {
            var status = scripted.Status;
            var (reason, body) = status switch
            {
                200 => ("OK", "stub-ok"),
                201 => ("Created", "stub-created"),
                202 => ("Accepted", "stub-accepted"),
                204 => ("No Content", ""),
                400 => ("Bad Request", "stub-bad-request"),
                401 or 403 => ("Unauthorized", "stub-unauthorized-or-forbidden-detail"),
                404 => ("Not Found", "stub-not-found"),
                422 => ("Unprocessable Entity", "stub-unprocessable"),
                429 => ("Too Many Requests", "stub-rate-limited"),
                _ => ("Internal Server Error", "stub-server-error"),
            };

            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var head =
                $"HTTP/1.1 {status} {reason}\r\n"
                + "Content-Type: text/plain; charset=utf-8\r\n"
                + (scripted.RetryAfter is { } retryAfter ? $"Retry-After: {retryAfter}\r\n" : "")
                + $"Content-Length: {bodyBytes.Length}\r\n"
                + "\r\n";

            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _shutdown.Token);
            if (status != 204 && bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, _shutdown.Token);
            }

            await stream.FlushAsync(_shutdown.Token);
        }

        private static int IndexOfHeaderEnd(MemoryStream data) =>
            Encoding.ASCII.GetString(data.ToArray()).IndexOf("\r\n\r\n", StringComparison.Ordinal);
    }
}
