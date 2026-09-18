using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using VibeMeter.Core;
using VibeMeter.Providers.Google;
using Xunit;

namespace VibeMeter.Tests.Providers.Google;

/// <summary>
/// Where the Google provider gets its configured accounts from, and what it
/// reports when it has one.
/// </summary>
/// <remarks>
/// <para>
/// The defect these cover: the configured-account list used to be a
/// process-wide <c>static</c> that only the WPF view model's constructor
/// assigned, so the roster belonged to the PROCESS rather than to the host that
/// built the provider. A host that never assigned it - or that had its value
/// overwritten by another host in the same process - reported
/// <see cref="ProviderState.NotConfigured"/> for an account sitting in the
/// settings file, and the tray app publishes the same reading it shows, so that
/// answer went to the collection API and masked a good reading from a host that
/// had read the account correctly.
/// </para>
/// <para>
/// Nothing here reaches the network or the local Antigravity install: the stub
/// auth reports no auto-detected token and throws instead of exchanging one.
/// </para>
/// </remarks>
[Collection(GoogleProviderCollection.Name)]
public sealed class GoogleProviderAccountSourceTests
{
    private const string SyntheticToken = "1//synthetic-refresh-token-for-tests-0000000000";

    [Fact]
    public async Task Each_provider_resolves_the_accounts_of_the_host_that_built_it()
    {
        // Two hosts in one process, as the tray app and a second view model would be.
        var trayHost = new GoogleProvider(
            new NoLocalAntigravity(),
            new FixedGoogleAccountSource(new[] { Account("tray@example.com") }));

        var hostWithNoAccounts = new GoogleProvider(
            new NoLocalAntigravity(),
            EmptyGoogleAccountSource.Instance);

        // Deliberately resolved second, and deliberately the empty one first:
        // with shared state, whichever ran last decided for both.
        Assert.Empty(await hostWithNoAccounts.ResolveRosterAsync());

        var trayRoster = await trayHost.ResolveRosterAsync();
        Assert.Equal("tray@example.com", Assert.Single(trayRoster).Email);
    }

    [Fact]
    public async Task A_host_with_a_configured_account_never_reports_not_configured()
    {
        var provider = new GoogleProvider(
            new NoLocalAntigravity(),
            new FixedGoogleAccountSource(new[] { Account("someone@example.com") }));

        var usage = await provider.FetchAsync();

        // The account is unusable in a test (there is no network), so the honest
        // answer is Error. What it must NOT be is NotConfigured: that is the
        // state the tray app published over the agent's good reading.
        Assert.Equal(ProviderState.Error, usage.State);
        Assert.Contains("someone@example.com", usage.ErrorMessage);
        Assert.Empty(usage.Gauges);
    }

    [Fact]
    public async Task A_host_with_no_accounts_and_no_antigravity_still_reports_not_configured()
    {
        var provider = new GoogleProvider(new NoLocalAntigravity(), EmptyGoogleAccountSource.Instance);

        var usage = await provider.FetchAsync();

        Assert.Equal(ProviderState.NotConfigured, usage.State);
    }

    [Fact]
    public async Task An_account_whose_stored_token_could_not_be_opened_is_left_out_of_the_roster()
    {
        // What GoogleAccountProtection produces on a roamed profile: the email
        // is still known, the credential is not. Offering it would guarantee a
        // failed fetch, and on a single-account machine it would replace
        // "add this account again" with an auth error.
        var unusable = new GoogleAccount
        {
            Email = "roamed@example.com",
            ProtectedRefreshToken = "blob-from-another-profile",
            NeedsReauthentication = true,
        };

        var provider = new GoogleProvider(
            new NoLocalAntigravity(), new FixedGoogleAccountSource(new[] { unusable }));

        Assert.Empty(await provider.ResolveRosterAsync());
        Assert.Equal(ProviderState.NotConfigured, (await provider.FetchAsync()).State);
    }

    [Fact]
    public async Task The_account_source_is_asked_on_every_fetch_not_captured_once()
    {
        var accounts = new List<GoogleAccount>();
        var provider = new GoogleProvider(
            new NoLocalAntigravity(),
            new FixedGoogleAccountSource(() => accounts));

        Assert.Empty(await provider.ResolveRosterAsync());

        // Adding an account in Settings must take effect on the next refresh,
        // without anything having to remember to re-seed the provider.
        accounts.Add(Account("added-later@example.com"));

        Assert.Equal("added-later@example.com", Assert.Single(await provider.ResolveRosterAsync()).Email);
    }

    private static GoogleAccount Account(string email) => new()
    {
        Email = email,
        ProtectedRefreshToken = "protected-blob",
        RefreshToken = SyntheticToken,
    };

    /// <summary>
    /// A machine with no Antigravity install and no network. Both overrides
    /// matter: the first keeps the test off the real credential store, the
    /// second keeps it off oauth2.googleapis.com.
    /// </summary>
    private sealed class NoLocalAntigravity : GoogleAuth
    {
        public override string? GetRefreshToken() => null;

        public override Task<string?> GetAccountEmailAsync() => Task.FromResult<string?>(null);

        public override Task<string> GetAccessTokenForAccountAsync(
            string refreshToken, CancellationToken ct = default) =>
            throw new InvalidOperationException("no token endpoint in tests");
    }
}

/// <summary>
/// Groups the test classes that touch <see cref="GoogleProvider"/>'s remaining
/// static carousel state (<c>Accounts</c> and <c>ActiveAccountIndex</c>, which
/// the WPF card reads). That state is process-global, so running these classes
/// in parallel would let them clobber one another.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class GoogleProviderCollection
{
    public const string Name = "google-provider-static-state";
}
