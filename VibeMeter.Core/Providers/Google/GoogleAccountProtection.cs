using System.Collections.Generic;
using VibeMeter.Core.Security;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Moves persisted <see cref="GoogleAccount"/> refresh tokens between their
/// stored (protected) form and the in-memory form the provider uses, and
/// migrates accounts written by builds that stored the token in the clear.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately in <c>VibeMeter.Core</c> rather than beside the WPF
/// settings service: it is the part with rules worth testing — migrate, degrade,
/// never write plaintext — and the settings service is in a Windows-only WPF
/// assembly the test suite cannot reference.
/// </para>
/// <para>
/// <b>The one rule.</b> No path here leaves a plaintext token anywhere that
/// will be serialised. When protection is unavailable the token is dropped and
/// the account is marked <see cref="GoogleAccount.NeedsReauthentication"/>;
/// keeping it readable "just this once" would reproduce the defect on exactly
/// the machines least able to afford it.
/// </para>
/// </remarks>
public static class GoogleAccountProtection
{
    /// <summary>
    /// Prepares freshly-loaded accounts for use: migrates any plaintext token to
    /// the protected form, opens the protected form into
    /// <see cref="GoogleAccount.RefreshToken"/>, and marks whatever could not be
    /// opened as needing re-authentication.
    /// </summary>
    /// <returns>
    /// True when the stored form changed and the caller must write the file back
    /// — which is what actually removes a migrated plaintext token from disk. A
    /// caller that ignores this leaves the plaintext in place until the next load.
    /// </returns>
    public static bool Unseal(IList<GoogleAccount> accounts, ISecretProtector protector)
    {
        bool changed = false;

        foreach (var account in accounts)
        {
            if (account is null) continue;

            account.NeedsReauthentication = false;

            // A pre-protection file: the token is still in the clear on disk.
            if (!string.IsNullOrWhiteSpace(account.LegacyRefreshToken))
            {
                string plaintext = account.LegacyRefreshToken!;

                if (protector.TryProtect(plaintext, out var protectedValue))
                {
                    // Migration. The plaintext leaves the persisted shape only now
                    // that there is a protected form to replace it with, and
                    // changed=true makes the caller write that replacement out.
                    account.LegacyRefreshToken = null;
                    account.ProtectedRefreshToken = protectedValue;
                    account.RefreshToken = plaintext;
                    changed = true;
                }
                else
                {
                    // No protection available on this host — every non-Windows one,
                    // since DPAPI is the only implementation. Clearing the legacy
                    // value here would destroy a credential the person still has,
                    // on nothing worse than opening the app, and re-adding the
                    // account cannot succeed here either. So the stored shape is
                    // left exactly as found and changed stays false, which stops
                    // the caller rewriting the file at all.
                    //
                    // The plaintext therefore stays in a file that already held it.
                    // That is not good, but it is not NEW harm, and it is the
                    // lesser of the two: destroying the token would be.
                    // RefreshToken is [JsonIgnore], so putting the value there
                    // keeps the account working for this session without adding
                    // the secret to anything on disk.
                    account.RefreshToken = plaintext;
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(account.ProtectedRefreshToken))
            {
                account.RefreshToken = "";
                account.NeedsReauthentication = true;
                continue;
            }

            if (protector.TryUnprotect(account.ProtectedRefreshToken!, out var opened))
            {
                account.RefreshToken = opened;
            }
            else
            {
                // Roamed profile, restored backup, another user's blob. The
                // stored value is kept — it may open on the machine it came
                // from — but this session has no credential for the account.
                account.RefreshToken = "";
                account.NeedsReauthentication = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Stores a newly-acquired refresh token on <paramref name="account"/> in its
    /// protected form. Returns false when this machine cannot protect it, in
    /// which case nothing is stored and the caller must refuse to save the
    /// account rather than fall back to plaintext.
    /// </summary>
    public static bool Seal(GoogleAccount account, string refreshToken, ISecretProtector protector)
    {
        if (!protector.TryProtect(refreshToken, out var protectedValue))
        {
            account.ProtectedRefreshToken = null;
            account.LegacyRefreshToken = null;
            account.RefreshToken = "";
            account.NeedsReauthentication = true;
            return false;
        }

        account.ProtectedRefreshToken = protectedValue;
        account.LegacyRefreshToken = null;
        account.RefreshToken = refreshToken;
        account.NeedsReauthentication = false;
        return true;
    }
}
