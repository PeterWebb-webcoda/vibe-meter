using System.Net;
using System.Text;
using VibeMeter.Core;
using VibeMeter.Providers.Claude;
using Xunit;

namespace VibeMeter.Tests.Providers;

/// <summary>
/// The Claude account key comes from the account that owns the sign-in token, not from
/// whatever <c>.claude.json</c> names at that moment. Regression cover for 2026-11-13, when
/// the two disagreed for ninety minutes and one account's usage was published under the
/// other's key. No test reaches the network, and every identifier here is synthetic.
/// </summary>
public sealed class ClaudeTokenAccountKeyTests
{
    private const string TokenA = "sk-ant-oat01-SYNTHETIC-TOKEN-A-FOR-TESTS-ONLY";
    private const string TokenB = "sk-ant-oat01-SYNTHETIC-TOKEN-B-FOR-TESTS-ONLY";
    private const string OrgUuid = "11111111-1111-4111-8111-111111111111";
    private const string AccountUuid = "22222222-2222-4222-8222-222222222222";
    private const string OtherAccountUuid = "33333333-3333-4333-8333-333333333333";

    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private static string Profile(string accountUuid) => $$"""
        {
          "account": { "uuid": "{{accountUuid}}", "email": "nobody@example.invalid", "full_name": "Nobody" },
          "organization": { "uuid": "{{OrgUuid}}", "name": "Synthetic" }
        }
        """;

    [Fact]
    public async Task The_key_is_derived_from_the_profile_the_token_returns()
    {
        using var handler = new ProfileHandler(_ => (HttpStatusCode.OK, Profile(AccountUuid)));
        using var client = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

        var key = await client.GetAccountKeyAsync(TokenA);

        Assert.Equal(AccountKey.Derive("claude", OrgUuid, AccountUuid), key);
        Assert.Equal(ClaudeApiClient.ProfileUrl, handler.Requests.Single().Url);
        Assert.Equal(TokenA, handler.Requests.Single().Bearer);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_failed_profile_read_yields_no_key(HttpStatusCode status)
    {
        using var handler = new ProfileHandler(_ => (status, "{}"));
        using var client = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

        Assert.Null(await client.GetAccountKeyAsync(TokenA));
    }

    [Fact]
    public async Task A_profile_without_an_account_yields_no_key()
    {
        using var handler = new ProfileHandler(_ => (HttpStatusCode.OK, "{\"organization\":{\"uuid\":\"" + OrgUuid + "\"}}"));
        using var client = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

        Assert.Null(await client.GetAccountKeyAsync(TokenA));
    }

    [Fact]
    public async Task One_request_per_token_however_many_polls()
    {
        using var handler = new ProfileHandler(_ => (HttpStatusCode.OK, Profile(AccountUuid)));
        var resolver = new ClaudeTokenAccountKey();
        var credential = new ClaudeCredential { AccessToken = TokenA };

        var first = await resolver.ResolveAsync(credential, liveSucceeded: true, Factory(handler), Now);
        var second = await resolver.ResolveAsync(credential, liveSucceeded: true, Factory(handler), Now.AddMinutes(1));

        // Remembered even on a poll where the live source did not answer.
        var third = await resolver.ResolveAsync(credential, liveSucceeded: false, Factory(handler), Now.AddMinutes(2));

        Assert.Equal(AccountKey.Derive("claude", OrgUuid, AccountUuid), first);
        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task A_different_token_is_asked_again()
    {
        using var handler = new ProfileHandler(bearer =>
            (HttpStatusCode.OK, Profile(bearer == TokenA ? AccountUuid : OtherAccountUuid)));
        var resolver = new ClaudeTokenAccountKey();

        var a = await resolver.ResolveAsync(new ClaudeCredential { AccessToken = TokenA }, true, Factory(handler), Now);
        var b = await resolver.ResolveAsync(new ClaudeCredential { AccessToken = TokenB }, true, Factory(handler), Now.AddMinutes(1));

        Assert.Equal(AccountKey.Derive("claude", OrgUuid, AccountUuid), a);
        Assert.Equal(AccountKey.Derive("claude", OrgUuid, OtherAccountUuid), b);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Nothing_is_sent_without_a_token_or_when_the_live_source_did_not_answer()
    {
        using var handler = new ProfileHandler(_ => (HttpStatusCode.OK, Profile(AccountUuid)));
        var resolver = new ClaudeTokenAccountKey();

        Assert.Null(await resolver.ResolveAsync(null, true, Factory(handler), Now));
        Assert.Null(await resolver.ResolveAsync(new ClaudeCredential(), true, Factory(handler), Now));
        Assert.Null(await resolver.ResolveAsync(new ClaudeCredential { AccessToken = TokenA }, false, Factory(handler), Now));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task A_failed_lookup_is_not_retried_until_the_pause_has_passed()
    {
        var status = HttpStatusCode.TooManyRequests;
        using var handler = new ProfileHandler(_ => (status, Profile(AccountUuid)));
        var resolver = new ClaudeTokenAccountKey();
        var credential = new ClaudeCredential { AccessToken = TokenA };

        Assert.Null(await resolver.ResolveAsync(credential, true, Factory(handler), Now));
        Assert.Null(await resolver.ResolveAsync(credential, true, Factory(handler), Now.AddMinutes(5)));
        Assert.Single(handler.Requests);

        status = HttpStatusCode.OK;
        var later = Now + ClaudeTokenAccountKey.RetryAfterFailure;
        Assert.NotNull(await resolver.ResolveAsync(credential, true, Factory(handler), later));
        Assert.Equal(2, handler.Requests.Count);
    }

    private static Func<ClaudeApiClient> Factory(HttpMessageHandler handler) =>
        () => new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

    private sealed class ProfileHandler(Func<string?, (HttpStatusCode Status, string Body)> respond)
        : HttpMessageHandler
    {
        public List<(string Url, string? Bearer)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bearer = request.Headers.Authorization?.Parameter;
            Requests.Add((request.RequestUri!.ToString(), bearer));
            var (status, body) = respond(bearer);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
