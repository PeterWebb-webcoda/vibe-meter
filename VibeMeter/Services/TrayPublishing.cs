using System;
using System.IO;
using VibeMeter.Models;
using VibeMeter.Publishing;

namespace VibeMeter.Services;

/// <summary>
/// Turns the tray app's own settings and paths into a
/// <see cref="DesktopPublishHost"/> — or into nothing at all, which is the
/// default. Everything host-specific about publishing lives here; the library
/// itself never derives a directory or reads a setting.
/// </summary>
internal static class TrayPublishing
{
    /// <summary>
    /// THIS host's offline queue, deliberately not the agent's.
    /// </summary>
    /// <remarks>
    /// Queue entries are coordinated by file name with no cross-process
    /// locking, so an installed agent and this tray app sharing one directory
    /// would race each other's flushes: both would read the same entry, both
    /// would POST it, and whichever deleted it second would fail. Each host
    /// therefore names its own directory — the agent uses
    /// <c>VibeMeter\VibeMeter.Agent\offline-queue</c>, and this is its sibling.
    /// (Spelt "VibeMeter.Tray" rather than the assembly name, which is plain
    /// "VibeMeter" and would nest the app's folder inside itself.)
    /// </remarks>
    public static string QueueDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VibeMeter",
        "VibeMeter.Tray",
        "offline-queue");

    /// <summary>
    /// Where MSAL caches the signed-in credential. Shared with the agent on
    /// purpose: the cache helper takes a cross-process lock, so unlike the
    /// queue this is safe to share — and sharing it means signing in once on a
    /// machine that runs both hosts.
    /// </summary>
    public static string TokenCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VibeMeter");

    /// <summary>
    /// Builds the publish host from saved settings, or returns
    /// <see langword="null"/> when this machine is not publishing. Nothing here
    /// touches the network or prompts for a sign-in: the first token is
    /// requested by the first publish, long after startup.
    /// </summary>
    /// <param name="notifySignIn">
    /// Shows the device-code message. Called from the publishing thread, so an
    /// implementation that touches the UI must marshal it itself.
    /// </param>
    public static DesktopPublishHost? TryStart(SettingsData settings, Action<string> notifySignIn) =>
        DesktopPublishHost.TryCreate(
            new DesktopPublishSettings(
                settings.PublishEnabled,
                settings.PublishApiBaseUrl,
                settings.PublishClientId,
                settings.PublishTenantId,
                settings.PublishScope),
            QueueDirectory,
            TokenCacheDirectory,
            ErrorLogPublishLog.Instance,
            notifySignIn);
}
