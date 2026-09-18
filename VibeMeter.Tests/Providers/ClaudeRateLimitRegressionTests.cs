using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VibeMeter.Core;
using VibeMeter.Providers.Claude;
using Xunit;

namespace VibeMeter.Tests.Providers;

/// <summary>
/// The two behaviours that let the live Claude source fail silently for an hour on
/// 2026-09-18, pinned at the production entry point (<see cref="ClaudeProvider.FetchAsync"/>)
/// rather than at a seam: a 429 was filed as an anonymous failure, and the very next poll
/// came straight back to the endpoint the server had just asked it to leave alone.
/// </summary>
/// <remarks>
/// <para>
/// Written before the fix and run against the committed client to confirm it failed
/// there: two consecutive polls made two requests, and the 429 came back as
/// <c>ClaudeApiOutcome.Failed</c> with no wait attached. It deliberately uses only
/// surface that existed before the fix so that it can be run against either version.
/// </para>
/// <para>
/// No test reaches the network: the transport is a stubbed <see cref="HttpMessageHandler"/>.
/// Every token here is synthetic.
/// </para>
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class ClaudeRateLimitRegressionTests : IDisposable
{
    private const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";
    private const string SyntheticToken = "sk-ant-oat01-SYNTHETIC-REGRESSION-TESTS-ONLY-5555555555-DO-NOT-LOG";

    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Providers", "Claude", "Fixtures");

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "vibemeter-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }
    }

    [Fact]
    public async Task TheSameProviderPolledTwiceAfterA429_ContactsTheEndpointOnce_AndStillReports()
    {
        // The mechanism that sustained the block: a per-address rate rule answers 429 with
        // Retry-After roughly an hour ahead, the tray polls once a minute, and a provider
        // that forgets the 429 the moment it has fallen back to a file is back on the
        // endpoint sixty seconds later. Both hosts keep ONE provider instance for the life
        // of the process, so that instance is where the server's wait has to be remembered.
        var dir = WriteCredentialsAndCache();
        using var handler = new CountingHandler(() => RateLimited(retryAfterSeconds: 3505));
        var provider = new ClaudeProvider(
            new ClaudeAuth(),
            () => new ClaudeApiClient(new HttpClient(handler, disposeHandler: false)));

        var first = await WithConfigDirAsync(dir, () => provider.FetchAsync());
        var second = await WithConfigDirAsync(dir, () => provider.FetchAsync());

        // The card keeps working off the file both times: falling back is correct.
        Assert.Equal(ProviderState.Ok, first.State);
        Assert.Equal(ProviderState.Ok, second.State);
        Assert.Equal("Claude Code CLI cache", first.SourceLabel);
        Assert.Equal("Claude Code CLI cache", second.SourceLabel);

        // But the endpoint that said to stay away for 3505 s is contacted exactly once.
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A429WithRetryAfter_IsNotFiledAsAnAnonymousFailure()
    {
        // Folding a 429 into the same bucket as a timeout or a 502 throws away the one
        // piece of information the server volunteered (how long to wait) and with it any
        // chance of the failure naming itself in a log.
        using var handler = new CountingHandler(() => RateLimited(retryAfterSeconds: 3505));
        using var client = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

        var result = await client.GetUsageAsync(SyntheticToken);

        Assert.NotEqual(ClaudeApiOutcome.Success, result.Outcome);
        Assert.NotEqual(ClaudeApiOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.Contains("429", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("3505", result.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, result.Detail!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------------- helpers

    private string WriteCredentialsAndCache()
    {
        Directory.CreateDirectory(_scratch);
        var expiry = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(_scratch, ".credentials.json"), $$"""
            {
              "claudeAiOauth": {
                "accessToken": "{{SyntheticToken}}",
                "expiresAt": {{expiry}},
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_5x"
              }
            }
            """);
        File.Copy(Path.Combine(FixtureDirectory, "gladux-usage_cache.json"), Path.Combine(_scratch, "usage_cache.json"));
        return _scratch;
    }

    /// <summary>The response observed from the real endpoint, shape for shape, body invented.</summary>
    private static HttpResponseMessage RateLimited(int retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                "{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"Rate limited. Please try again later.\"}}",
                Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    private static async Task<T> WithConfigDirAsync<T>(string dir, Func<Task<T>> body)
    {
        var saved = Environment.GetEnvironmentVariable(ConfigDirVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConfigDirVariable, dir);
            return await body();
        }
        finally
        {
            Environment.SetEnvironmentVariable(ConfigDirVariable, saved);
        }
    }

    /// <summary>Answers every call from the factory and counts them. Never reaches a socket.</summary>
    private sealed class CountingHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond());
        }
    }
}
