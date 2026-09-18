using System.Net;
using System.Text;
using VibeMeter.Providers.Claude;
using Xunit;

namespace VibeMeter.Tests.Providers;

/// <summary>
/// The live Claude usage source: reading the stored credential, fetching over HTTP, and
/// falling back cleanly when any of that is unavailable.
/// </summary>
/// <remarks>
/// <para>
/// The bug this source exists to fix is that neither local file is dependable.
/// <c>usage_cache.json</c> is not maintained by Claude Code — it is written only on an
/// explicit <c>/usage</c> or a limit-approaching warning, and was measured sitting
/// untouched for over a day across 2h10m of continuous heavy use. The desktop history
/// samples at a 30-minute median against a 20-minute staleness gate. Every user whose
/// figures silently froze had a working install and a stale file.
/// </para>
/// <para>
/// So the tests here are in two halves, and the second matters as much as the first:
/// that the live reading wins when it is available, and that a machine without it behaves
/// exactly as it did before this source existed. Adding a source must never make a
/// currently-working machine worse.
/// </para>
/// <para>
/// No test reaches the network: the transport is a stubbed
/// <see cref="HttpMessageHandler"/>, following <c>SnapshotPublisherTests</c>' rule that a
/// test never leaves the machine. Every token here is synthetic — assembled in this file,
/// never read from a real credential.
/// </para>
/// </remarks>
[Collection(EnvironmentCollection.Name)]
public sealed class ClaudeLiveUsageTests : IDisposable
{
    private const string ConfigDirVariable = "CLAUDE_CONFIG_DIR";

    /// <summary>
    /// A token shaped like the real thing and belonging to nobody. Several tests assert
    /// that this exact string appears in nothing reported, so it must stay distinctive.
    /// </summary>
    private const string SyntheticToken = "sk-ant-oat01-SYNTHETIC-FOR-TESTS-ONLY-0123456789-DO-NOT-LOG";

    /// <summary>A second synthetic secret, standing in for another service's entry.</summary>
    private const string SyntheticMcpToken = "synthetic-mcp-token-belonging-to-another-service";

    private static readonly string FixtureDirectory =
        Path.Combine(AppContext.BaseDirectory, "Providers", "Claude", "Fixtures");

    private static string GladuxCliCache => Path.Combine(FixtureDirectory, "gladux-usage_cache.json");

    /// <summary>The CLI fixture's own figures: <c>five_hour.utilization</c> 14, seven-day 77.</summary>
    private const int CliCacheFiveHourUsed = 14;

    /// <summary>The live stub's figures, chosen to differ from the fixture's.</summary>
    private const int LiveFiveHourUsed = 42;

    /// <summary>
    /// 2026-09-17T02:07:48.590Z in Unix <b>milliseconds</b>. Read as seconds it lands in
    /// the year 56000; a seconds value read as milliseconds lands in 1970.
    /// </summary>
    private const long ExpiryUnixMilliseconds = 1789697268590;

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

    // ------------------------------------------------------ reading the stored credential

    [Fact]
    public async Task TheCredentialIsReadFromTheConfigDirectory_HonouringClaudeConfigDir()
    {
        var dir = WriteCredentials(CredentialsJson());

        var credential = await WithConfigDirAsync(dir, () => new ClaudeAuth().GetCredentialAsync());

        Assert.NotNull(credential);
        Assert.True(credential!.HasToken);
        Assert.Equal(SyntheticToken, credential.AccessToken);

        // The credential must relocate with the usage cache it sits beside, not stay behind
        // on the profile drive.
        Assert.Equal(Path.Combine(dir, ".credentials.json"),
            WithConfigDir(dir, () => ClaudeAuth.CredentialsFilePath));
    }

    [Fact]
    public async Task ExpiresAt_IsUnixMilliseconds_NotSeconds()
    {
        var dir = WriteCredentials(CredentialsJson(expiresAt: ExpiryUnixMilliseconds));

        var credential = await WithConfigDirAsync(dir, () => new ClaudeAuth().GetCredentialAsync());

        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(ExpiryUnixMilliseconds),
            credential!.ExpiresAt);

        // The two ways of getting the unit wrong, ruled out explicitly rather than implied.
        Assert.Equal(2026, credential.ExpiresAt!.Value.UtcDateTime.Year);
        Assert.NotEqual(1970, credential.ExpiresAt.Value.UtcDateTime.Year);
    }

    [Fact]
    public void AnOutOfRangeExpiry_IsNullRatherThanThrowing()
    {
        Assert.Null(ClaudeAuth.FromUnixMilliseconds(long.MaxValue));
        Assert.Null(ClaudeAuth.FromUnixMilliseconds(null));
        Assert.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(ExpiryUnixMilliseconds),
            ClaudeAuth.FromUnixMilliseconds(ExpiryUnixMilliseconds));
    }

    [Fact]
    public async Task AnAbsentCredentialFile_IsNullRatherThanAnError()
    {
        Directory.CreateDirectory(_scratch);

        Assert.Null(await WithConfigDirAsync(_scratch, () => new ClaudeAuth().GetCredentialAsync()));
    }

    [Fact]
    public async Task AnUnreadableCredentialFile_IsNullRatherThanAnError()
    {
        var dir = WriteCredentials("{ this is not json");

        Assert.Null(await WithConfigDirAsync(dir, () => new ClaudeAuth().GetCredentialAsync()));
    }

    [Fact]
    public async Task ACredentialFileWithNoClaudeEntry_IsNull()
    {
        // The file exists, but everything in it belongs to other services.
        var dir = WriteCredentials($$"""
            { "mcpOAuth": { "some-server": { "accessToken": "{{SyntheticMcpToken}}" } } }
            """);

        Assert.Null(await WithConfigDirAsync(dir, () => new ClaudeAuth().GetCredentialAsync()));
    }

    [Fact]
    public async Task OnlyTheClaudeEntryIsBound_AndTheFileIsLeftExactlyAsItWas()
    {
        // The real file carries many unrelated mcpOAuth.* entries for other services.
        var json = $$"""
            {
              "claudeAiOauth": {
                "accessToken": "{{SyntheticToken}}",
                "refreshToken": "synthetic-refresh-token-never-bound",
                "expiresAt": {{ExpiryUnixMilliseconds}},
                "subscriptionType": "max",
                "rateLimitTier": "default_claude_max_5x"
              },
              "mcpOAuth": {
                "server-one": { "accessToken": "{{SyntheticMcpToken}}", "expiresAt": 1 },
                "server-two": { "accessToken": "{{SyntheticMcpToken}}", "expiresAt": 2 }
              }
            }
            """;
        var dir = WriteCredentials(json);
        var path = Path.Combine(dir, ".credentials.json");
        var before = await File.ReadAllBytesAsync(path);

        var credential = await WithConfigDirAsync(dir, () => new ClaudeAuth().GetCredentialAsync());

        // Ours, and only ours.
        Assert.Equal(SyntheticToken, credential!.AccessToken);
        Assert.Equal("max", credential.SubscriptionType);
        Assert.Equal("default_claude_max_5x", credential.RateLimitTier);

        // Nothing this code returns carries another service's secret...
        Assert.DoesNotContain(SyntheticMcpToken, Report(credential), StringComparison.Ordinal);

        // ...and the file is only ever read, byte for byte.
        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    // ------------------------------------------------------------------ the live fetch

    [Fact]
    public async Task ALiveReadingOutranksTheCliCache()
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Serving(HttpStatusCode.OK, UsageResponse());

        var attempt = await AttemptAsync(dir, handler);
        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, GladuxCliCache, null);

        Assert.Equal(ClaudeUsageSource.LiveApi, chosen!.Source);
        Assert.Equal(LiveFiveHourUsed, chosen.FiveHourPercentUsed);

        // The premise, asserted rather than assumed: the CLI cache really does hold a
        // different figure, so this is a selection and not a coincidence.
        var fileOnly = await ClaudeUsageSources.ReadBestAsync(null, GladuxCliCache, null);
        Assert.Equal(CliCacheFiveHourUsed, fileOnly!.FiveHourPercentUsed);
    }

    [Fact]
    public async Task ALiveReadingIsObservedNow_AndIsNotASampledSource()
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Serving(HttpStatusCode.OK, UsageResponse());

        var before = DateTime.Now;
        var attempt = await AttemptAsync(dir, handler);
        var after = DateTime.Now;

        // A live fetch observes the value now. That is precisely why it fixes the staleness
        // the file sources suffer from, so it is worth pinning.
        Assert.InRange(attempt.Snapshot!.ObservedAt, before, after);
        Assert.True(attempt.Snapshot.IsCurrentAt(DateTime.Now));

        // Not sampled: we asked and it answered, so there is no cadence to be between.
        Assert.Null(attempt.Snapshot.SamplingInterval);
        Assert.False(attempt.Snapshot.ResetTimesAreApproximate);
        Assert.Equal(ClaudeUsageSources.LiveSourceLabel, attempt.Snapshot.SourceLabel);
    }

    [Fact]
    public async Task TheRequestCarriesTheDocumentedHeaders()
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Serving(HttpStatusCode.OK, UsageResponse());

        await AttemptAsync(dir, handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(ClaudeApiClient.UsageUrl, request.Url);
        Assert.Equal($"Bearer {SyntheticToken}", request.Authorization);
        Assert.Equal("oauth-2025-04-20", request.AnthropicBeta);
        Assert.Contains("application/json", request.Accept, StringComparison.Ordinal);
        Assert.Contains("claude-cli", request.UserAgent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MembersTheResponseCarriesButTheModelDoesNotDeclare_AreIgnored()
    {
        // The real response also carries seven_day_opus, seven_day_sonnet, extra_usage and
        // spend. Ignoring them is correct: the endpoint may add fields at any time, and
        // doing so must not cost us a reading.
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Serving(HttpStatusCode.OK, $$"""
            {
              "five_hour": { "utilization": {{LiveFiveHourUsed}}, "resets_at": "2026-09-18T15:00:00+00:00" },
              "seven_day": { "utilization": 8, "resets_at": "2026-09-22T00:00:00+00:00" },
              "seven_day_opus": { "utilization": 3, "resets_at": null },
              "seven_day_sonnet": null,
              "extra_usage": { "enabled": true, "spend_cents": 1234 },
              "spend": { "amount": 5, "currency": "USD" },
              "something_invented_tomorrow": [1, 2, 3]
            }
            """);

        var attempt = await AttemptAsync(dir, handler);

        Assert.Equal(LiveFiveHourUsed, attempt.Snapshot!.FiveHourPercentUsed);
        Assert.Equal(8, attempt.Snapshot.SevenDayPercentUsed);
    }

    // --------------------------------------------------------------- falling back cleanly

    [Fact]
    public async Task WithNoCredential_TheCliCacheStillWins_ExactlyAsBefore()
    {
        Directory.CreateDirectory(_scratch);
        using var handler = new FailIfCalledHandler();

        var attempt = await AttemptAsync(_scratch, handler);

        Assert.False(handler.WasCalled);
        Assert.Null(attempt.Snapshot);

        // No credential is an ordinary state — a desktop-only user never has one — so there
        // is nothing to tell the user about.
        Assert.Null(attempt.Note);

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, GladuxCliCache, null);
        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
        Assert.Equal(CliCacheFiveHourUsed, chosen.FiveHourPercentUsed);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task WhenTheCallFails_TheCliCacheIsUsed_AndNothingIsSaid(HttpStatusCode status)
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Serving(status, "{}");

        var attempt = await AttemptAsync(dir, handler);

        Assert.Null(attempt.Snapshot);

        // A transient failure is not the user's to act on, and the local files are there
        // precisely for it.
        Assert.Null(attempt.Note);

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, GladuxCliCache, null);
        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
    }

    [Fact]
    public async Task WhenTheNetworkIsUnreachable_TheCliCacheIsUsed()
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Throwing(new HttpRequestException("no such host"));

        var attempt = await AttemptAsync(dir, handler);

        Assert.Null(attempt.Snapshot);
        Assert.Null(attempt.Note);

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, GladuxCliCache, null);
        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
    }

    [Fact]
    public async Task AnExpiredToken_ShortCircuitsWithoutACall_AndNamesTheFix()
    {
        var expired = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
        var dir = WriteCredentials(CredentialsJson(expiresAt: expired));
        using var handler = new FailIfCalledHandler();

        var attempt = await AttemptAsync(dir, handler);

        // The point of reading the expiry at all: a lapsed token earns an opaque 401, which
        // reads as "Claude is broken" rather than "your sign-in lapsed".
        Assert.False(handler.WasCalled);
        Assert.Null(attempt.Snapshot);
        Assert.NotNull(attempt.Note);
        Assert.Contains("expired", attempt.Note!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/login", attempt.Note, StringComparison.Ordinal);

        // And it still falls back, so the card keeps working.
        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, GladuxCliCache, null);
        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task ARejectedCredential_IsDistinctFromATransientFailure(HttpStatusCode status)
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Serving(status, """{"error":"unauthorized"}""");

        var attempt = await AttemptAsync(dir, handler);

        // Only the user can fix this one, and the fix is a command — so unlike a 5xx it is
        // worth saying.
        Assert.NotNull(attempt.Note);
        Assert.Contains("/login", attempt.Note!, StringComparison.Ordinal);

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, GladuxCliCache, null);
        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
    }

    // ------------------------------------------------------------------ the token itself

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.OK)]
    public async Task TheTokenNeverAppearsInAnythingReported(HttpStatusCode status)
    {
        var dir = WriteCredentials(CredentialsJson());

        // The body echoes the token back at us, as a rejection response plausibly might.
        using var handler = StubHandler.Serving(status, $$"""
            { "error": "bad token {{SyntheticToken}}", "five_hour": { "utilization": 1 } }
            """);

        var attempt = await AttemptAsync(dir, handler);

        // Whatever the outcome, nothing we hand upwards carries it. The credential object
        // holds the token by design — it is what the request is built from — so it is the
        // reported strings that are checked here.
        Assert.DoesNotContain(SyntheticToken, attempt.Note ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, attempt.Snapshot?.SourcePath ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticToken, attempt.Snapshot?.SourceLabel ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureDescriptionNamesTheEndpoint_NeverTheTokenOrTheBody()
    {
        using var handler = StubHandler.Serving(HttpStatusCode.InternalServerError, $$"""
            { "trace": "{{SyntheticToken}}" }
            """);
        using var client = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

        var result = await client.GetUsageAsync(SyntheticToken);

        Assert.Equal(ClaudeApiOutcome.Failed, result.Outcome);
        Assert.NotNull(result.Detail);
        Assert.DoesNotContain(SyntheticToken, result.Detail!, StringComparison.Ordinal);
        Assert.DoesNotContain("trace", result.Detail!, StringComparison.Ordinal);
        Assert.Contains("500", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnreachableEndpointIsDescribedWithoutQuotingTheException()
    {
        // An HttpRequestException can quote the request it failed on, and that request
        // carries the bearer token.
        using var handler = StubHandler.Throwing(new HttpRequestException($"failed: {SyntheticToken}"));
        using var client = new ClaudeApiClient(new HttpClient(handler, disposeHandler: false));

        var result = await client.GetUsageAsync(SyntheticToken);

        Assert.Equal(ClaudeApiOutcome.Failed, result.Outcome);
        Assert.DoesNotContain(SyntheticToken, result.Detail!, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------- the plan label

    [Fact]
    public void TheRateLimitTierStaysTheMoreSpecificLabel()
    {
        // Why subscriptionType is a fallback and not a replacement: it is stated rather
        // than inferred, but it is also coarser — it cannot tell 5x from 20x.
        Assert.Equal("Claude Max 5x", ClaudeAuth.FriendlyTier("default_claude_max_5x"));
        Assert.Equal("Claude Max", ClaudeAuth.FriendlySubscription("max"));
        Assert.Equal("Claude Pro", ClaudeAuth.FriendlySubscription("pro"));

        // Already named, so not named twice.
        Assert.Equal("Claude Max", ClaudeAuth.FriendlySubscription("claude_max"));
        Assert.Null(ClaudeAuth.FriendlySubscription(null));
        Assert.Null(ClaudeAuth.FriendlySubscription("  "));
    }

    // ------------------------------------------------------------------------- helpers

    /// <summary>Everything a credential exposes, flattened — for "this must not appear" checks.</summary>
    private static string Report(ClaudeCredential credential) =>
        string.Join('|', credential.AccessToken, credential.SubscriptionType,
            credential.RateLimitTier, credential.ExpiresAt?.ToString("O"));

    /// <summary>
    /// A credential file. The expiry defaults to a week out, so only the test that means to
    /// exercise a lapsed token gets one.
    /// </summary>
    private static string CredentialsJson(long? expiresAt = null)
    {
        var expiry = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
        return CredentialsJsonAt(expiry);
    }

    private static string CredentialsJsonAt(long expiresAt) => $$"""
        {
          "claudeAiOauth": {
            "accessToken": "{{SyntheticToken}}",
            "refreshToken": "synthetic-refresh-token-never-bound",
            "expiresAt": {{expiresAt}},
            "refreshTokenExpiresAt": {{expiresAt}},
            "scopes": ["user:inference", "user:profile"],
            "subscriptionType": "max",
            "rateLimitTier": "default_claude_max_5x"
          }
        }
        """;

    /// <summary>Writes a credential file into the scratch config directory, and returns it.</summary>
    private string WriteCredentials(string json)
    {
        Directory.CreateDirectory(_scratch);
        File.WriteAllText(Path.Combine(_scratch, ".credentials.json"), json);
        return _scratch;
    }

    /// <summary>Runs one live attempt against a stubbed transport. Never reaches the network.</summary>
    private static Task<ClaudeProvider.LiveUsageAttempt> AttemptAsync(string configDir, HttpMessageHandler handler) =>
        WithConfigDirAsync(configDir, () => ClaudeProvider.TryFetchLiveAsync(
            new ClaudeAuth(),
            () => new ClaudeApiClient(new HttpClient(handler, disposeHandler: false))));

    private static string UsageResponse() => $$"""
        {
          "five_hour": { "utilization": {{LiveFiveHourUsed}}, "resets_at": "2026-09-18T15:00:00+00:00" },
          "seven_day": { "utilization": 8, "resets_at": "2026-09-22T00:00:00+00:00" },
          "limits": [
            {
              "kind": "weekly_scoped",
              "percent": 3,
              "resets_at": "2026-09-22T00:00:00+00:00",
              "scope": { "model": { "id": "opus", "display_name": "Opus" } }
            }
          ]
        }
        """;

    private static T WithConfigDir<T>(string dir, Func<T> body) =>
        WithConfigDirAsync(dir, () => Task.FromResult(body())).GetAwaiter().GetResult();

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

    /// <summary>Records what was asked for, and answers with a script. Never reaches a socket.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly Exception? _throw;

        private StubHandler(HttpStatusCode status, string body, Exception? toThrow)
        {
            _status = status;
            _body = body;
            _throw = toThrow;
        }

        public static StubHandler Serving(HttpStatusCode status, string body) => new(status, body, null);

        public static StubHandler Throwing(Exception toThrow) =>
            new(HttpStatusCode.OK, string.Empty, toThrow);

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest(
                Method: request.Method,
                Url: request.RequestUri?.ToString() ?? "",
                Authorization: request.Headers.Authorization?.ToString(),
                AnthropicBeta: request.Headers.TryGetValues("anthropic-beta", out var beta)
                    ? string.Join(',', beta)
                    : null,
                Accept: request.Headers.Accept.ToString(),
                UserAgent: request.Headers.UserAgent.ToString()));

            if (_throw is not null) throw _throw;

            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        string Url,
        string? Authorization,
        string? AnthropicBeta,
        string Accept,
        string UserAgent);

    private sealed class FailIfCalledHandler : HttpMessageHandler
    {
        public bool WasCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            throw new InvalidOperationException("The usage endpoint must not be contacted in this case.");
        }
    }
}
