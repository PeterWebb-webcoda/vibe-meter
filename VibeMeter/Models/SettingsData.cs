using System.Collections.Generic;
using VibeMeter.Providers.Google;

namespace VibeMeter.Models;

/// <summary>
/// Persisted user preferences, stored in %APPDATA%\VibeMeter\settings.json.
/// <see cref="ProviderEnabled"/> lets the user turn individual providers on/off.
/// </summary>
public class SettingsData
{
    public int TintIndex { get; set; } = 0;
    public bool AutoRefreshEnabled { get; set; } = true;
    public int RefreshIntervalSeconds { get; set; } = 60;
    public string MeterStyleName { get; set; } = "Circular";
    public bool LaunchAtStartup { get; set; } = false;
    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>Show the widget as a slim horizontal strip instead of the full panel.</summary>
    public bool CompactMode { get; set; } = false;

    /// <summary>Per-provider enable flags, keyed by provider Id. Absent = enabled.</summary>
    public Dictionary<string, bool> ProviderEnabled { get; set; } = new();

    /// <summary>
    /// VibeMeter-owned Google accounts (email + OAuth refresh token), added via the
    /// in-app "Add account" flow. The Google card's carousel shows these <i>plus</i> the
    /// account the Antigravity IDE is signed into (auto-detected at runtime and de-duped
    /// by email), so adding an account here never hides the IDE's own one.
    /// </summary>
    public List<GoogleAccount> GoogleAccounts { get; set; } = new();

    /// <summary>True unless explicitly disabled in <see cref="ProviderEnabled"/>.</summary>
    public bool IsProviderEnabled(string providerId) =>
        !ProviderEnabled.TryGetValue(providerId, out var enabled) || enabled;
}
