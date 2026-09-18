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
    /// Opt in to sending this machine's usage to the collection API off each
    /// refresh, so the companion mobile app can show it. Off by default and
    /// deliberately so: publishing puts this machine's provider ids, states and
    /// percentages on someone's server, which is nobody's business to decide on
    /// the user's behalf.
    /// </summary>
    public bool PublishEnabled { get; set; } = false;

    /// <summary>The collection API root, e.g. <c>https://api.example.com</c>. Required when <see cref="PublishEnabled"/>.</summary>
    public string PublishApiBaseUrl { get; set; } = "";

    /// <summary>
    /// Entra application (client) id of the public-client registration this app
    /// signs in with. Required when <see cref="PublishEnabled"/>.
    /// </summary>
    /// <remarks>
    /// Not a secret — nor are <see cref="PublishTenantId"/> and
    /// <see cref="PublishScope"/> — but deliberately not defaulted in source:
    /// baking one organisation's directory into the build would tie a general
    /// tool to that tenant. The cached credential itself lives in MSAL's
    /// encrypted store, never in this file.
    /// </remarks>
    public string PublishClientId { get; set; } = "";

    /// <summary>Entra directory (tenant) id. Required when <see cref="PublishEnabled"/>.</summary>
    public string PublishTenantId { get; set; } = "";

    /// <summary>Delegated scope to request, e.g. <c>api://&lt;api-app-id&gt;/Usage.Write</c>. Required when <see cref="PublishEnabled"/>.</summary>
    public string PublishScope { get; set; } = "";

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
