using System.Net;
using System.Text;
using VibeMeter.Providers.Codex;
using Xunit;

namespace VibeMeter.Tests.Providers;

/// <summary>
/// Covers the guards that decide whether the Codex credential file is touched
/// at all.
/// </summary>
/// <remarks>
/// These matter more than the happy path. The file belongs to another
/// application, so the failure this suite is really protecting against is
/// rewriting a real user's Codex sign-in. Every test here asserts that nothing
/// happens: no test may reach the file or the network.
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class CodexTokenRefreshTests
{
    [Fact]
    public void AutomaticRefresh_IsOffUnlessAskedFor()
    {
        WithEnabled(null, () => Assert.False(CodexTokenRefresh.IsEnabled));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    public void AutomaticRefresh_IsOnlyOnForRecognisedValues(string value)
    {
        WithEnabled(value, () => Assert.True(CodexTokenRefresh.IsEnabled));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("yes")]
    [InlineData("")]
    public void AutomaticRefresh_IgnoresAnythingElse(string value)
    {
        WithEnabled(value, () => Assert.False(CodexTokenRefresh.IsEnabled));
    }

    [Fact]
    public async Task WhenDisabled_NothingIsAttempted()
    {
        using var handler = new FailIfCalledHandler();
        using var client = new HttpClient(handler);

        await WithEnabledAsync(null, async () =>
        {
            // Expiry already passed: only the opt-out should stop this.
            var result = await CodexTokenRefresh.TryRefreshAsync(client, DateTimeOffset.UtcNow.AddDays(-1));
            Assert.Null(result);
        });

        Assert.False(handler.WasCalled);
    }

    [Fact]
    public async Task WhenExpiryIsFarAway_NothingIsAttempted()
    {
        // A refresh token spent every cycle is a refresh token eventually lost.
        using var handler = new FailIfCalledHandler();
        using var client = new HttpClient(handler);

        await WithEnabledAsync("1", async () =>
        {
            var result = await CodexTokenRefresh.TryRefreshAsync(client, DateTimeOffset.UtcNow.AddDays(9));
            Assert.Null(result);
        });

        Assert.False(handler.WasCalled);
    }

    [Fact]
    public void ClientId_ComesFromTheTokenItself()
    {
        var token = Jwt("""{"client_id":"app_Example123","exp":1790435630}""");
        Assert.Equal("app_Example123", CodexAuth.ReadClientId(token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public void ClientId_IsNullRatherThanThrowingOnRubbish(string? token)
    {
        Assert.Null(CodexAuth.ReadClientId(token));
    }

    [Fact]
    public void ClientId_IsNullWhenTheClaimIsAbsent()
    {
        Assert.Null(CodexAuth.ReadClientId(Jwt("""{"exp":1790435630}""")));
    }

    /// <summary>Builds a token whose payload is real base64url, padding included.</summary>
    private static string Jwt(string payloadJson)
    {
        var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        return $"header.{payload}.signature";
    }

    private sealed class FailIfCalledHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("The refresh endpoint must not be contacted in this case.");
        }
    }

    private static void WithEnabled(string? value, Action body) =>
        WithEnabledAsync(value, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    private static async Task WithEnabledAsync(string? value, Func<Task> body)
    {
        var saved = Environment.GetEnvironmentVariable(CodexTokenRefresh.EnabledVariable);
        try
        {
            Environment.SetEnvironmentVariable(CodexTokenRefresh.EnabledVariable, value);
            await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(CodexTokenRefresh.EnabledVariable, saved);
        }
    }
}
