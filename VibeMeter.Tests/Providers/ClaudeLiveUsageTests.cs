using System.Net;
using System.Text;
using VibeMeter.Core;
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

    private static string LinuxCliCache => Path.Combine(FixtureDirectory, "linux-usage_cache.json");

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
        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, LinuxCliCache, null);

        Assert.Equal(ClaudeUsageSource.LiveApi, chosen!.Source);
        Assert.Equal(LiveFiveHourUsed, chosen.FiveHourPercentUsed);

        // The premise, asserted rather than assumed: the CLI cache really does hold a
        // different figure, so this is a selection and not a coincidence.
        var fileOnly = await ClaudeUsageSources.ReadBestAsync(null, LinuxCliCache, null);
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

        // The product token is the verified call's — the one this machine's statusline
        // script sends successfully with the same credential — and the comment says who we
        // really are. It must not announce itself as an obsolete build of the CLI: not because
        // that was ever shown to be refused (the 2026-09-18 failure was a per-address 429,
        // whatever the user agent), but because it is a needless difference from the request
        // proven to work, and an untrue one.
        Assert.StartsWith("claude-code/2.0.32", request.UserAgent, StringComparison.Ordinal);
        Assert.Contains("vibe-meter", request.UserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain("claude-cli/", request.UserAgent, StringComparison.Ordinal);
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

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, LinuxCliCache, null);
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

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, LinuxCliCache, null);
        Assert.Equal(ClaudeUsageSource.CliCache, chosen!.Source);
    }

    // ---------------------------------------------------- saying why, where it can be seen

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task WhenTheCallFails_TheReasonIsCarriedForTheLog_NamingTheStatus(HttpStatusCode status)
    {
        // The bug this pins: a live source that fails on EVERY cycle used to be invisible.
        // The card stays quiet — correctly, the files are there for this — but the only
        // trace anywhere was the fallback file's age tripping the freshness gate, a line
        // that named the file that was too old and said nothing about why the source that
        // is never too old was passed over. So the reason now travels with the attempt.
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Serving(status, "{}");

        var attempt = await AttemptAsync(dir, handler);

        Assert.Null(attempt.Snapshot);
        Assert.NotNull(attempt.Diagnostic);
        Assert.Contains(ClaudeUsageSources.LiveSourceLabel, attempt.Diagnostic!, StringComparison.Ordinal);
        Assert.Contains(((int)status).ToString(), attempt.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenTheNetworkIsUnreachable_TheReasonIsCarriedForTheLog()
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Throwing(new HttpRequestException("no such host"));

        var attempt = await AttemptAsync(dir, handler);

        Assert.NotNull(attempt.Diagnostic);
        Assert.Contains("could not be reached", attempt.Diagnostic!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExpiredToken_IsCarriedForTheLogToo()
    {
        var expired = DateTimeOffset.UtcNow.AddDays(-2).ToUnixTimeMilliseconds();
        var dir = WriteCredentials(CredentialsJson(expiresAt: expired));
        using var handler = new FailIfCalledHandler();

        var attempt = await AttemptAsync(dir, handler);

        Assert.NotNull(attempt.Diagnostic);
        Assert.Contains("expired", attempt.Diagnostic!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ASuccessfulCall_AndAMachineWithNoSignIn_HaveNothingToExplain()
    {
        // No sign-in is an ordinary state, not a fault; and a live reading that won needs
        // no explanation. Neither may produce a line in anyone's log.
        var withCredential = WriteCredentials(CredentialsJson());
        using var ok = StubHandler.Serving(HttpStatusCode.OK, UsageResponse());
        Assert.Null((await AttemptAsync(withCredential, ok)).Diagnostic);

        var noCredential = Path.Combine(_scratch, "no-sign-in");
        Directory.CreateDirectory(noCredential);
        using var never = new FailIfCalledHandler();
        Assert.Null((await AttemptAsync(noCredential, never)).Diagnostic);
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task TheReadingItselfSaysWhichSourceWon_AndWhyTheLiveOneDidNot(
        HttpStatusCode status, bool liveWins)
    {
        // End to end through the provider: the reading the hosts log and publish carries
        // both the winning source and, when the live source lost, the reason it lost.
        var dir = WriteCredentials(CredentialsJson());
        File.Copy(LinuxCliCache, Path.Combine(dir, "usage_cache.json"));
        using var handler = StubHandler.Serving(status, UsageResponse());
        var provider = new ClaudeProvider(
            new ClaudeAuth(),
            () => new ClaudeApiClient(new HttpClient(handler, disposeHandler: false)));

        var usage = await WithConfigDirAsync(dir, () => provider.FetchAsync());

        Assert.Equal(ProviderState.Ok, usage.State);
        if (liveWins)
        {
            Assert.Equal(ClaudeUsageSources.LiveSourceLabel, usage.SourceLabel);
            Assert.Null(usage.SourceDiagnostic);
        }
        else
        {
            Assert.Equal("Claude Code CLI cache", usage.SourceLabel);
            Assert.NotNull(usage.SourceDiagnostic);
            Assert.Contains("503", usage.SourceDiagnostic!, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticToken, usage.SourceDiagnostic!, StringComparison.Ordinal);
        }
    }

    // ------------------------------------------- the WPF host's threading model, pinned

    [Fact]
    public async Task TheWholeFetchCompletesUnderASingleThreadedSynchronizationContext_LikeAWpfDispatcher()
    {
        // The tray app awaits FetchAsync from a DispatcherTimer tick with no
        // ConfigureAwait(false) anywhere above the HTTP client, so every continuation in the
        // provider is posted back to ONE thread — the same shape as a WPF dispatcher. Any
        // blocking wait on that path would deadlock, and a deadlock would surface as the
        // client's timeout: a silent Failed, a silent fallback, and a Claude card that quietly
        // stops being live only in the WPF host. This runs the real path under exactly that
        // constraint, against a transport that completes OFF that thread, and requires the
        // live reading to come back — on the pump thread, as the dispatcher would deliver it.
        var dir = WriteCredentials(CredentialsJson());
        File.Copy(LinuxCliCache, Path.Combine(dir, "usage_cache.json"));
        using var handler = new OffThreadHandler(HttpStatusCode.OK, UsageResponse());
        var provider = new ClaudeProvider(
            new ClaudeAuth(),
            () => new ClaudeApiClient(new HttpClient(handler, disposeHandler: false)));

        using var pump = new SingleThreadedSynchronizationContext();
        var (usage, resumedOn) = await WithConfigDirAsync(dir, () => pump.RunAsync(async () =>
        {
            var result = await provider.FetchAsync();
            return (result, Environment.CurrentManagedThreadId);
        }, TimeSpan.FromSeconds(20)));

        Assert.Equal(ProviderState.Ok, usage.State);
        Assert.Equal(ClaudeUsageSources.LiveSourceLabel, usage.SourceLabel);
        Assert.Null(usage.SourceDiagnostic);

        // The premise of the test, asserted rather than assumed: the transport really did
        // answer from another thread, and the continuations really did hop back.
        Assert.NotEqual(pump.ThreadId, handler.AnsweredOnThreadId);
        Assert.Equal(pump.ThreadId, resumedOn);
        Assert.True(pump.PostCount > 0, "no continuation was posted to the context, so nothing was exercised");
    }

    [Fact]
    public async Task WhenTheNetworkIsUnreachable_TheCliCacheIsUsed()
    {
        var dir = WriteCredentials(CredentialsJson());
        using var handler = StubHandler.Throwing(new HttpRequestException("no such host"));

        var attempt = await AttemptAsync(dir, handler);

        Assert.Null(attempt.Snapshot);
        Assert.Null(attempt.Note);

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, LinuxCliCache, null);
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
        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, LinuxCliCache, null);
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

        var chosen = await ClaudeUsageSources.ReadBestAsync(attempt.Snapshot, LinuxCliCache, null);
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
        Assert.DoesNotContain(SyntheticToken, attempt.Diagnostic ?? "", StringComparison.Ordinal);
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

    /// <summary>
    /// Answers from a thread-pool thread after a genuine asynchronous hop, the way a real
    /// socket completion does — so the continuation above it has to be marshalled back.
    /// </summary>
    private sealed class OffThreadHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int AnsweredOnThreadId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            AnsweredOnThreadId = Environment.CurrentManagedThreadId;
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    /// <summary>
    /// A <see cref="SynchronizationContext"/> with the one property of a WPF dispatcher that
    /// matters to async code: every posted continuation runs on a single dedicated thread,
    /// in order, and that thread does nothing else. Code that blocks that thread waiting on
    /// a continuation that needs it deadlocks — here, into the caller's timeout rather than
    /// a hung test run.
    /// </summary>
    private sealed class SingleThreadedSynchronizationContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;
        private int _postCount;

        public SingleThreadedSynchronizationContext()
        {
            _thread = new Thread(Pump) { IsBackground = true, Name = "test-dispatcher" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback d, object? state)
        {
            Interlocked.Increment(ref _postCount);
            _queue.Add((d, state));
        }

        public override void Send(SendOrPostCallback d, object? state)
        {
            // Dispatcher.Invoke semantics: inline on the dispatcher thread, blocking otherwise.
            if (Environment.CurrentManagedThreadId == ThreadId)
            {
                d(state);
                return;
            }

            using var done = new ManualResetEventSlim();
            _queue.Add(((SendOrPostCallback)(s => { d(s); done.Set(); }), state));
            done.Wait();
        }

        public override SynchronizationContext CreateCopy() => this;

        /// <summary>
        /// Starts <paramref name="body"/> ON the pump thread, with this context current, and
        /// waits for it from the calling thread — bounded, so a deadlock fails instead of hangs.
        /// </summary>
        public async Task<T> RunAsync<T>(Func<Task<T>> body, TimeSpan timeout)
        {
            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    completion.TrySetResult(await body());
                }
                catch (Exception ex)
                {
                    completion.TrySetException(ex);
                }
            }, null);

            return await completion.Task.WaitAsync(timeout);
        }

        private void Pump()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
            {
                callback(state);
            }
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            if (!_thread.Join(TimeSpan.FromSeconds(5)))
            {
                // A pump still busy at teardown is a test failure elsewhere; do not hang here.
            }
            _queue.Dispose();
        }
    }
}
