using System.Net;
using System.Net.Http.Headers;
using System.Text;
using VibeMeter.Providers.Claude;
using Xunit;

namespace VibeMeter.Tests.Providers;

/// <summary>
/// The live Claude usage source under a rate limit: a 429 is classified as such, its
/// <c>Retry-After</c> is honoured, ordinary failures back off on a doubling schedule, and a
/// success clears the lot.
/// </summary>
/// <remarks>
/// <para>
/// The fault this guards against was observed on 2026-09-18: Anthropic's usage endpoint
/// answered <c>429</c> from Cloudflare with <c>Retry-After: 3505</c> to every request from
/// one address, credential or not, for about an hour at a time. The first version of the
/// client filed that under "Failed", said nothing, and the provider came straight back on
/// the next one-minute poll — which is part of what keeps such a rule tripped. The tray
/// therefore never once obtained a live reading, the CLI cache aged past the freshness gate,
/// and the provider vanished from the published snapshots.
/// </para>
/// <para>
/// No test reaches the network: the transport is a stubbed <see cref="HttpMessageHandler"/>
/// and the clock is a value passed in. Every token here is synthetic.
/// </para>
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class ClaudeLiveSourceBackoffTests : IDisposable
{
    private const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";
    private const string SyntheticToken = "sk-ant-oat01-SYNTHETIC-BACKOFF-TESTS-ONLY-9876543210-DO-NOT-LOG";

    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 22, 39, 0, TimeSpan.FromHours(10));

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

    // ------------------------------------------------------------------ the schedule

    [Fact]
    public void ARateLimit_PausesForExactlyTheServersRetryAfter()
    {
        var backoff = new ClaudeLiveSourceBackoff();

        var until = backoff.RecordRateLimited(T0, TimeSpan.FromSeconds(3505), "HTTP 429");

        Assert.Equal(T0.AddSeconds(3505), until);
        Assert.True(backoff.IsPaused(T0));
        Assert.True(backoff.IsPaused(T0.AddSeconds(3504)));
        Assert.False(backoff.IsPaused(T0.AddSeconds(3505)));
        Assert.Equal(1, backoff.ConsecutiveFailures);
    }

    [Fact]
    public void ARateLimitWithNoRetryAfter_PausesForTheDefault()
    {
        var backoff = new ClaudeLiveSourceBackoff();

        var until = backoff.RecordRateLimited(T0, retryAfter: null, "HTTP 429");

        Assert.Equal(T0 + ClaudeLiveSourceBackoff.DefaultRateLimitPause, until);
    }

    [Fact]
    public void ARetryAfterOutsideTheSaneRange_IsClamped()
    {
        // A day-long header must not silence the source for a day on the server's say-so...
        var tooLong = new ClaudeLiveSourceBackoff().RecordRateLimited(T0, TimeSpan.FromDays(1), "HTTP 429");
        Assert.Equal(T0 + ClaudeLiveSourceBackoff.MaximumRateLimitPause, tooLong);

        // ...and a one-second header must not mean "hammer".
        var tooShort = new ClaudeLiveSourceBackoff().RecordRateLimited(T0, TimeSpan.FromSeconds(1), "HTTP 429");
        Assert.Equal(T0 + ClaudeLiveSourceBackoff.MinimumPause, tooShort);
    }

    [Fact]
    public void OrdinaryFailures_DoubleFromOneMinute_AndCapAtFifteen()
    {
        var backoff = new ClaudeLiveSourceBackoff();
        var expectedMinutes = new[] { 1, 2, 4, 8, 15, 15, 15 };

        var now = T0;
        foreach (var minutes in expectedMinutes)
        {
            var until = backoff.RecordFailure(now, "HTTP 502");
            Assert.Equal(now.AddMinutes(minutes), until);
            now = until;
        }

        Assert.Equal(expectedMinutes.Length, backoff.ConsecutiveFailures);
    }

    [Fact]
    public void ASuccess_ClearsThePause_AndReportsWhatItRecoveredFrom()
    {
        var backoff = new ClaudeLiveSourceBackoff();
        backoff.RecordFailure(T0, "timeout");
        backoff.RecordRateLimited(T0.AddMinutes(1), TimeSpan.FromMinutes(30), "HTTP 429");
        Assert.True(backoff.IsPaused(T0.AddMinutes(2)));

        var recoveredFrom = backoff.RecordSuccess();

        Assert.Equal(2, recoveredFrom);
        Assert.Equal(0, backoff.ConsecutiveFailures);
        Assert.False(backoff.IsPaused(T0.AddMinutes(2)));
        Assert.Null(backoff.PausedUntil);
        Assert.Null(backoff.PausedReason);
        Assert.Null(backoff.PausedNote);
    }

    // ---------------------------------------------------------- Retry-After parsing

    [Fact]
    public void RetryAfter_IsReadAsSecondsOrAsAnHttpDate()
    {
        using var seconds = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        seconds.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(3505));
        Assert.Equal(TimeSpan.FromSeconds(3505), ClaudeApiClient.ReadRetryAfter(seconds));

        using var dated = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        dated.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(10));
        var wait = ClaudeApiClient.ReadRetryAfter(dated);
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value, TimeSpan.FromMinutes(9), TimeSpan.FromMinutes(10));

        using var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Null(ClaudeApiClient.ReadRetryAfter(past));

        using var absent = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Null(ClaudeApiClient.ReadRetryAfter(absent));
    }

    [Fact]
    public async Task A429_IsItsOwnOutcome_CarryingTheWait_AndNeverTheBody()
    {
        using var handler = new ScriptedHandler(_ => RateLimited(3505, body: $"{{\"trace\":\"{SyntheticToken}\"}}"));
        using var client = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

        var result = await client.GetUsageAsync(SyntheticToken);

        Assert.Equal(ClaudeApiOutcome.RateLimited, result.Outcome);
        Assert.Equal(TimeSpan.FromSeconds(3505), result.RetryAfter);
        Assert.Contains("429", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("3505", result.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, result.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("trace", result.Detail!, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ the provider's use

    [Fact]
    public async Task After_A429_TheEndpointIsLeftAlone_UntilRetryAfterElapses_ThenTriedAgain()
    {
        var dir = WriteCredentials();
        var backoff = new ClaudeLiveSourceBackoff();
        using var handler = new ScriptedHandler(call => call == 1
            ? RateLimited(3505)
            : Ok(UsageResponse(fiveHourUsed: 42)));

        // Cycle 1: the 429. No reading, nothing asked of the user, but the log is told
        // what happened and for how long the source is being rested.
        var first = await AttemptAsync(dir, handler, backoff, T0);
        Assert.Null(first.Snapshot);
        Assert.Null(first.Note);
        Assert.NotNull(first.Diagnostic);
        Assert.Contains("429", first.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains("paused until", first.Diagnostic!, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);

        // Cycles 2..n, inside the window: the endpoint is NOT contacted, and the diagnostic
        // is the very same string, so a host that logs on change logs nothing.
        var second = await AttemptAsync(dir, handler, backoff, T0.AddMinutes(1));
        var third = await AttemptAsync(dir, handler, backoff, T0.AddMinutes(58));
        Assert.Null(second.Snapshot);
        Assert.Null(third.Snapshot);
        Assert.Equal(first.Diagnostic, second.Diagnostic);
        Assert.Equal(first.Diagnostic, third.Diagnostic);
        Assert.Equal(1, handler.Calls);

        // The moment the server's wait is up: one more request, and this time a reading.
        var fourth = await AttemptAsync(dir, handler, backoff, T0.AddSeconds(3505));
        Assert.Equal(2, handler.Calls);
        Assert.NotNull(fourth.Snapshot);
        Assert.Equal(ClaudeUsageSource.LiveApi, fourth.Snapshot!.Source);
        Assert.Equal(42, fourth.Snapshot.FiveHourPercentUsed);
        Assert.Null(fourth.Diagnostic);
        Assert.Equal(0, backoff.ConsecutiveFailures);
    }

    [Fact]
    public async Task AFreshBackoff_DoesNotHoldTheFirstAttemptBack()
    {
        // The agent's --dry-run is one cycle on a fresh provider. It must behave exactly as
        // before this class existed: try once, and use the answer.
        var dir = WriteCredentials();
        using var handler = new ScriptedHandler(_ => Ok(UsageResponse(fiveHourUsed: 7)));

        var attempt = await AttemptAsync(dir, handler, new ClaudeLiveSourceBackoff(), T0);

        Assert.Equal(1, handler.Calls);
        Assert.Equal(7, attempt.Snapshot!.FiveHourPercentUsed);
        Assert.Null(attempt.Diagnostic);
    }

    [Fact]
    public async Task ARejectedCredential_PausesOnTheDoublingSchedule_AndKeepsItsNoteMeanwhile()
    {
        var dir = WriteCredentials();
        var backoff = new ClaudeLiveSourceBackoff();
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"unauthorized\"}", Encoding.UTF8, "application/json"),
        });

        var first = await AttemptAsync(dir, handler, backoff, T0);
        Assert.NotNull(first.Note);
        Assert.Contains("/login", first.Note!, StringComparison.Ordinal);
        Assert.Contains("401", first.Diagnostic!, StringComparison.Ordinal);
        Assert.Equal(T0.AddMinutes(1), backoff.PausedUntil);

        // Thirty seconds later: no request, but the card is still told what to do.
        var paused = await AttemptAsync(dir, handler, backoff, T0.AddSeconds(30));
        Assert.Equal(1, handler.Calls);
        Assert.Equal(first.Note, paused.Note);
        Assert.Equal(first.Diagnostic, paused.Diagnostic);

        // A minute on: tried again, refused again, and the pause doubles.
        var second = await AttemptAsync(dir, handler, backoff, T0.AddMinutes(1));
        Assert.Equal(2, handler.Calls);
        Assert.Equal(T0.AddMinutes(3), backoff.PausedUntil);
        Assert.NotEqual(first.Diagnostic, second.Diagnostic);
    }

    [Fact]
    public async Task ATransientFailure_PausesBriefly_AndTheDiagnosticNamesIt()
    {
        var dir = WriteCredentials();
        var backoff = new ClaudeLiveSourceBackoff();
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        });

        var attempt = await AttemptAsync(dir, handler, backoff, T0);

        Assert.Null(attempt.Snapshot);
        Assert.Null(attempt.Note);
        Assert.Contains("502", attempt.Diagnostic!, StringComparison.Ordinal);
        Assert.Equal(T0 + ClaudeLiveSourceBackoff.MinimumPause, backoff.PausedUntil);
    }

    [Fact]
    public async Task NothingThePauseReports_CarriesTheToken()
    {
        var dir = WriteCredentials();
        var backoff = new ClaudeLiveSourceBackoff();
        using var handler = new ScriptedHandler(_ => RateLimited(60, body: $"{{\"bad\":\"{SyntheticToken}\"}}"));

        var attempt = await AttemptAsync(dir, handler, backoff, T0);
        var paused = await AttemptAsync(dir, handler, backoff, T0.AddSeconds(30));

        foreach (var text in new[] { attempt.Diagnostic, paused.Diagnostic, attempt.Note, backoff.PausedReason, backoff.PausedNote })
        {
            Assert.DoesNotContain(SyntheticToken, text ?? "", StringComparison.Ordinal);
        }
    }

    // ------------------------------------------------------------------------- helpers

    private static Task<ClaudeProvider.LiveUsageAttempt> AttemptAsync(
        string configDir, HttpMessageHandler handler, ClaudeLiveSourceBackoff backoff, DateTimeOffset now) =>
        WithConfigDirAsync(configDir, () => ClaudeProvider.TryFetchLiveAsync(
            new ClaudeAuth(),
            () => new ClaudeApiClient(new HttpClient(handler, disposeHandler: false)),
            backoff,
            now));

    private string WriteCredentials()
    {
        Directory.CreateDirectory(_scratch);
        var expiry = T0.AddDays(7).ToUnixTimeMilliseconds();
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
        return _scratch;
    }

    private static string UsageResponse(int fiveHourUsed) => $$"""
        {
          "five_hour": { "utilization": {{fiveHourUsed}}, "resets_at": "2026-09-18T15:00:00+00:00" },
          "seven_day": { "utilization": 8, "resets_at": "2026-09-22T00:00:00+00:00" }
        }
        """;

    private static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    /// <summary>The response observed from the real endpoint, shape for shape, body invented.</summary>
    private static HttpResponseMessage RateLimited(int retryAfterSeconds, string? body = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent(
                body ?? "{\"error\":{\"type\":\"rate_limit_error\",\"message\":\"Rate limited. Please try again later.\"}}",
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

    /// <summary>Answers each call from a script keyed by call number, and counts them. Never reaches a socket.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _script;

        public ScriptedHandler(Func<int, HttpResponseMessage> script) => _script = script;

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_script(Calls));
        }
    }
}
