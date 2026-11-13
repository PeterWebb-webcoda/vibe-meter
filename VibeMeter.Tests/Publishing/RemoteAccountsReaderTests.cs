using System.Net;
using System.Text;
using VibeMeter.Publishing;
using VibeMeter.Publishing.AccessToken;
using Xunit;

namespace VibeMeter.Tests.Publishing;

/// <summary>
/// RemoteAccountsReader against a stubbed <see cref="HttpMessageHandler"/>: nothing here
/// touches the network. Every outcome must come back as a result, never an exception,
/// because the caller is a detached publish cycle that has to carry on.
/// </summary>
public sealed class RemoteAccountsReaderTests
{
    private const string BearerToken = "stub-reader-bearer-token-0123456789abcdef-DO-NOT-LOG";
    private static readonly Uri BaseUrl = new("https://api.example.test");

    /// <summary>The documented shape, plus members this build has never heard of.</summary>
    private const string TwoAccountDocument = """
        {
          "schemaVersion": 2,
          "observedAt": "2026-11-13T09:36:19+00:00",
          "receivedAt": "2026-11-13T09:36:24+00:00",
          "someFutureTopLevelField": { "nested": [1, 2, 3] },
          "providers": [
            {
              "providerId": "claude",
              "futureProviderField": true,
              "accounts": [
                {
                  "accountId": "1",
                  "accountLabel": "work",
                  "state": "available",
                  "planLabel": "Plan A",
                  "gauges": [
                    { "id": "claude-5h", "title": "5h", "subtitle": "Claude Code", "percentRemaining": 100,
                      "resetAt": "2026-11-13T09:59:59+00:00", "resetWindowSeconds": 18000, "futureGaugeField": "x" }
                  ],
                  "observedAt": "2026-11-13T09:36:19+00:00",
                  "receivedAt": "2026-11-13T09:36:24+00:00"
                },
                {
                  "accountId": "2",
                  "accountLabel": null,
                  "state": "available",
                  "planLabel": null,
                  "gauges": [
                    { "id": "claude-week", "title": "Week", "subtitle": null, "percentRemaining": 42.5,
                      "resetAt": null, "resetWindowSeconds": null }
                  ],
                  "observedAt": null,
                  "receivedAt": "2026-11-13T09:30:00+00:00"
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public async Task A200_IsParsed_AndUnknownMembersAreIgnored()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, TwoAccountDocument);
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), handler);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.Success, result.Status);
        Assert.True(result.Succeeded);
        Assert.Equal(200, result.StatusCode);

        var response = Assert.IsType<RemoteAccountsResponse>(result.Response);
        Assert.Equal(2, response.SchemaVersion);
        var provider = Assert.Single(response.Providers!);
        Assert.Equal("claude", provider.ProviderId);
        Assert.Equal(2, provider.Accounts!.Count);

        var first = provider.Accounts[0];
        Assert.Equal("work", first.AccountLabel);
        Assert.Equal("Plan A", first.PlanLabel);
        var gauge = Assert.Single(first.Gauges!);
        Assert.Equal("claude-5h", gauge.Id);
        Assert.Equal(100m, gauge.PercentRemaining);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 9, 59, 59, TimeSpan.Zero), gauge.ResetAt);
        Assert.Equal(18000, gauge.ResetWindowSeconds);

        var second = provider.Accounts[1];
        Assert.Null(second.AccountLabel);
        Assert.Null(second.PlanLabel);
        Assert.Null(second.ObservedAt);
        Assert.Equal(42.5m, Assert.Single(second.Gauges!).PercentRemaining);
        Assert.Null(second.Gauges![0].ResetAt);
    }

    [Fact]
    public async Task TheRequest_IsAnAuthenticatedGetOfTheAccountsPath()
    {
        var handler = StubHandler.Returning(HttpStatusCode.NoContent, null);
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), handler);

        await reader.ReadAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.example.test/api/v1/ai-usage/accounts", request.Uri);
        Assert.Equal("Bearer " + BearerToken, request.Authorization);
    }

    [Fact]
    public async Task A204_IsAnEmptySuccess()
    {
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Returning(HttpStatusCode.NoContent, null));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.Empty, result.Status);
        Assert.True(result.Succeeded);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task A404_MeansAnOlderApi_NotAFault()
    {
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Returning(HttpStatusCode.NotFound, "Not Found"));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.NotSupported, result.Status);
        Assert.False(result.Succeeded);
        Assert.Equal(404, result.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AuthRejections_AreAuthFailures(HttpStatusCode status)
    {
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Returning(status, "denied"));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.AuthFailure, result.Status);
        Assert.Equal((int)status, result.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task OtherStatuses_AreQuietFailures(HttpStatusCode status)
    {
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Returning(status, "oops"));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.Failed, result.Status);
        Assert.Equal((int)status, result.StatusCode);
        Assert.Null(result.Response);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[1, 2, 3]")]
    [InlineData("""{ "providers": "should-be-an-array" }""")]
    [InlineData("null")]
    public async Task AMalformedBody_IsAQuietFailure(string body)
    {
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Returning(HttpStatusCode.OK, body));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.Failed, result.Status);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task ANetworkFailure_IsAQuietFailure()
    {
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Throwing(new HttpRequestException("no route")));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.Failed, result.Status);
        Assert.Null(result.StatusCode);
    }

    [Fact]
    public async Task ATimeout_IsAQuietFailure()
    {
        // HttpClient reports its own timeout as a TaskCanceledException that is NOT the
        // caller's cancellation; that must not escape as though the app were closing.
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Throwing(new TaskCanceledException("timed out")));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.Failed, result.Status);
    }

    [Fact]
    public async Task NoToken_IsAnAuthFailure_AndNothingIsSent()
    {
        var handler = StubHandler.Returning(HttpStatusCode.OK, TwoAccountDocument);
        using var reader = new RemoteAccountsReader(BaseUrl, new ThrowingToken(), handler);

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.Equal(RemoteAccountsReadStatus.AuthFailure, result.Status);
        Assert.Null(result.StatusCode);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TheCallersCancellation_IsHonoured()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Returning(HttpStatusCode.OK, TwoAccountDocument));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(cancelled.Token));
    }

    [Fact]
    public async Task NoResult_EverCarriesTheTokenOrTheBody()
    {
        // The result type has no free-text member at all; this pins that it stays so.
        using var reader = new RemoteAccountsReader(BaseUrl, new FixedToken(), StubHandler.Returning(HttpStatusCode.Unauthorized, "echo " + BearerToken));

        var result = await reader.ReadAsync(CancellationToken.None);

        Assert.DoesNotContain(BearerToken, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("echo", result.ToString(), StringComparison.Ordinal);
    }

    private sealed class FixedToken : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(BearerToken);
        }
    }

    private sealed class ThrowingToken : IAccessTokenProvider
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("not signed in");
    }

    private sealed record SeenRequest(HttpMethod Method, string? Uri, string? Authorization);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpResponseMessage> _respond;

        private StubHandler(Func<HttpResponseMessage> respond) => _respond = respond;

        public List<SeenRequest> Requests { get; } = new();

        public static StubHandler Returning(HttpStatusCode status, string? body) => new(() =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            return response;
        });

        public static StubHandler Throwing(Exception exception) => new(() => throw exception);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(new SeenRequest(request.Method, request.RequestUri?.ToString(), request.Headers.Authorization?.ToString()));
            return Task.FromResult(_respond());
        }
    }
}
