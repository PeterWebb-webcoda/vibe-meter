using System.Collections.Generic;

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

    /// <summary>Per-provider enable flags, keyed by provider Id. Absent = enabled.</summary>
    public Dictionary<string, bool> ProviderEnabled { get; set; } = new();

    /// <summary>True unless explicitly disabled in <see cref="ProviderEnabled"/>.</summary>
    public bool IsProviderEnabled(string providerId) =>
        !ProviderEnabled.TryGetValue(providerId, out var enabled) || enabled;
}
