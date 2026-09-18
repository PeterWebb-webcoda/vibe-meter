using System.Collections.Generic;
using System.Text.Json;
using VibeMeter.Core.Security;
using VibeMeter.Providers.Google;
using Xunit;

namespace VibeMeter.Tests.Providers.Google;

/// <summary>
/// The Google refresh token at rest: it is protected, a plaintext one left by an
/// older build is migrated away on load, and a machine that cannot protect or
/// open it says "add this account again" instead of falling back to plaintext.
/// </summary>
/// <remarks>
/// Every token here is synthetic and constructed in this file. Nothing reads the
/// real settings file, and nothing touches the network.
/// </remarks>
public sealed class GoogleAccountProtectionTests
{
    // Shaped like a Google refresh token ("1//" then URL-safe characters) so the
    // assertions about what does NOT appear in the serialised file are honest.
    // It is a literal in this test and authorises nothing.
    private const string SyntheticToken = "1//synthetic-refresh-token-for-tests-0000000000";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    // --- The protector itself ---

    [Fact]
    public void Dpapi_round_trips_a_secret_and_does_not_store_it_readably()
    {
        var protector = DpapiSecretProtector.Instance;

        Assert.True(protector.TryProtect(SyntheticToken, out var protectedValue));
        Assert.DoesNotContain(SyntheticToken, protectedValue);
        Assert.NotEqual(SyntheticToken, protectedValue);

        Assert.True(protector.TryUnprotect(protectedValue, out var opened));
        Assert.Equal(SyntheticToken, opened);
    }

    [Fact]
    public void Dpapi_refuses_a_value_it_did_not_produce_rather_than_throwing()
    {
        Assert.False(DpapiSecretProtector.Instance.TryUnprotect("not-base64-and-not-a-blob", out var opened));
        Assert.Equal("", opened);
    }

    [Fact]
    public void An_account_sealed_with_dpapi_opens_again_and_the_file_never_holds_the_token()
    {
        var account = new GoogleAccount { Email = "someone@example.com" };

        Assert.True(GoogleAccountProtection.Seal(account, SyntheticToken, DpapiSecretProtector.Instance));
        Assert.Equal(SyntheticToken, account.RefreshToken);
        Assert.DoesNotContain(SyntheticToken, JsonSerializer.Serialize(account, Json));

        // Round-trip through the file shape, as a real load does.
        var reloaded = JsonSerializer.Deserialize<GoogleAccount>(
            JsonSerializer.Serialize(account, Json), Json)!;
        Assert.Equal("", reloaded.RefreshToken);

        var list = new List<GoogleAccount> { reloaded };
        Assert.False(GoogleAccountProtection.Unseal(list, DpapiSecretProtector.Instance));
        Assert.Equal(SyntheticToken, reloaded.RefreshToken);
        Assert.False(reloaded.NeedsReauthentication);
    }

    // --- Migration of data already on disk ---

    [Fact]
    public void A_plaintext_token_from_an_older_settings_file_is_migrated_on_load()
    {
        var accounts = DeserialiseLegacyFile();

        bool changed = GoogleAccountProtection.Unseal(accounts, new ReversingProtector());

        // "changed" is what tells the settings service to write the file back.
        // Without that write the plaintext is still on disk.
        Assert.True(changed);
        var account = Assert.Single(accounts);
        Assert.Null(account.LegacyRefreshToken);
        Assert.NotNull(account.ProtectedRefreshToken);
        Assert.NotEqual(SyntheticToken, account.ProtectedRefreshToken);
        Assert.Equal(SyntheticToken, account.RefreshToken);
        Assert.False(account.NeedsReauthentication);
    }

    [Fact]
    public void The_migrated_file_no_longer_contains_the_plaintext_token()
    {
        var accounts = DeserialiseLegacyFile();
        GoogleAccountProtection.Unseal(accounts, new ReversingProtector());

        string rewritten = JsonSerializer.Serialize(accounts, Json);

        Assert.DoesNotContain(SyntheticToken, rewritten);
        // The property itself is gone, not merely emptied: an empty
        // "RefreshToken" would still read as "this is where the token lives".
        Assert.DoesNotContain("\"RefreshToken\"", rewritten);
        Assert.Contains("\"ProtectedRefreshToken\"", rewritten);
    }

    [Fact]
    public void An_already_protected_file_is_not_rewritten_on_every_load()
    {
        var accounts = DeserialiseLegacyFile();
        GoogleAccountProtection.Unseal(accounts, new ReversingProtector());

        var reloaded = JsonSerializer.Deserialize<List<GoogleAccount>>(
            JsonSerializer.Serialize(accounts, Json), Json)!;

        Assert.False(GoogleAccountProtection.Unseal(reloaded, new ReversingProtector()));
        Assert.Equal(SyntheticToken, Assert.Single(reloaded).RefreshToken);
    }

    // --- Degrading when protection is unavailable ---

    [Fact]
    public void A_failed_unprotect_degrades_to_needing_the_account_added_again()
    {
        var accounts = new List<GoogleAccount>
        {
            new() { Email = "someone@example.com", ProtectedRefreshToken = "blob-from-another-profile" },
        };

        bool changed = GoogleAccountProtection.Unseal(accounts, new UnavailableProtector());

        Assert.False(changed);
        var account = Assert.Single(accounts);
        Assert.True(account.NeedsReauthentication);
        Assert.Equal("", account.RefreshToken);
        // The stored blob is kept: it may still open on the machine it came from.
        Assert.Equal("blob-from-another-profile", account.ProtectedRefreshToken);
    }

    [Fact]
    public void A_failed_protect_during_migration_drops_the_token_rather_than_storing_it_readably()
    {
        var accounts = DeserialiseLegacyFile();

        bool changed = GoogleAccountProtection.Unseal(accounts, new UnavailableProtector());

        Assert.True(changed);
        var account = Assert.Single(accounts);
        Assert.True(account.NeedsReauthentication);
        Assert.Equal("", account.RefreshToken);
        Assert.Null(account.ProtectedRefreshToken);
        Assert.DoesNotContain(SyntheticToken, JsonSerializer.Serialize(accounts, Json));
    }

    [Fact]
    public void Sealing_a_new_token_on_a_machine_that_cannot_protect_stores_nothing()
    {
        var account = new GoogleAccount { Email = "someone@example.com" };

        Assert.False(GoogleAccountProtection.Seal(account, SyntheticToken, new UnavailableProtector()));
        Assert.Null(account.ProtectedRefreshToken);
        Assert.Null(account.LegacyRefreshToken);
        Assert.Equal("", account.RefreshToken);
        Assert.True(account.NeedsReauthentication);
        Assert.DoesNotContain(SyntheticToken, JsonSerializer.Serialize(account, Json));
    }

    /// <summary>The account list exactly as builds up to 0.5.1 wrote it.</summary>
    private static List<GoogleAccount> DeserialiseLegacyFile() =>
        JsonSerializer.Deserialize<List<GoogleAccount>>(
            "[ { \"Email\": \"someone@example.com\", \"RefreshToken\": \"" + SyntheticToken + "\" } ]",
            Json)!;

    /// <summary>
    /// A protector that is obviously not encryption - it reverses the string -
    /// so the migration assertions test the flow rather than DPAPI, and hold on
    /// a machine where DPAPI is unavailable.
    /// </summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public bool TryProtect(string plaintext, out string protectedValue)
        {
            var chars = plaintext.ToCharArray();
            System.Array.Reverse(chars);
            protectedValue = new string(chars);
            return true;
        }

        public bool TryUnprotect(string protectedValue, out string plaintext)
        {
            var chars = protectedValue.ToCharArray();
            System.Array.Reverse(chars);
            plaintext = new string(chars);
            return true;
        }
    }

    /// <summary>A machine where protection does not work - a roamed profile.</summary>
    private sealed class UnavailableProtector : ISecretProtector
    {
        public bool TryProtect(string plaintext, out string protectedValue)
        {
            protectedValue = "";
            return false;
        }

        public bool TryUnprotect(string protectedValue, out string plaintext)
        {
            plaintext = "";
            return false;
        }
    }
}
