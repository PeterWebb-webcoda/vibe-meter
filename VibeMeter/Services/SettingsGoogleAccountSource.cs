using System.Collections.Generic;
using VibeMeter.Providers.Google;

namespace VibeMeter.Services;

/// <summary>
/// The tray app's Google accounts, read from its settings file on every fetch.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it re-reads rather than caching.</b> The reading the tray app PUBLISHES
/// is the same object it puts on the card, so the two can only differ if the
/// provider's idea of the configured accounts differs from the settings file's.
/// Anything that populates the provider once — a constructor, a "sync" call —
/// makes that divergence possible: it depends on a call having happened before
/// the first refresh, and on nothing having changed since. Asking the file each
/// time removes the question. The file is under a kilobyte and a refresh is at
/// least thirty seconds apart, so this costs nothing worth measuring.
/// </para>
/// <para>
/// <see cref="SettingsService.Load"/> also unseals the stored tokens, so the
/// accounts handed over here carry a usable
/// <see cref="GoogleAccount.RefreshToken"/> — or, when the protected value
/// could not be opened on this machine, none at all and
/// <see cref="GoogleAccount.NeedsReauthentication"/> set, which the provider
/// skips.
/// </para>
/// </remarks>
public sealed class SettingsGoogleAccountSource : IGoogleAccountSource
{
    private readonly SettingsService _settingsService;

    public SettingsGoogleAccountSource(SettingsService settingsService) =>
        _settingsService = settingsService;

    public IReadOnlyList<GoogleAccount> GetConfiguredAccounts()
    {
        try
        {
            return _settingsService.Load().GoogleAccounts;
        }
        catch
        {
            // Load already returns defaults for a missing or unreadable file;
            // this is the belt-and-braces path, because a provider fetch must
            // never throw out of a settings read.
            return System.Array.Empty<GoogleAccount>();
        }
    }
}
