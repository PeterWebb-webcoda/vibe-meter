using System;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;

namespace VibeMeter.Avalonia;

/// <summary>
/// Answers whether this desktop can actually show a tray icon.
/// </summary>
/// <remarks>
/// <para>
/// Avalonia's only Linux tray backend is StatusNotifierItem, and it fails <em>silently</em>:
/// with no watcher on the session bus the icon simply never appears and nothing throws. That
/// matters here because the window is deliberately not shown at startup and does not appear in
/// the taskbar, so a silent tray failure leaves a process with no window, no icon and no way to
/// quit it short of <c>kill</c>.
/// </para>
/// <para>
/// The signal used is whether anything owns the well-known name
/// <c>org.kde.StatusNotifierWatcher</c>. That is not a heuristic: it is the precondition the SNI
/// backend itself needs, so it answers the actual question rather than correlating with it. A
/// timer waiting to "see if the icon appeared" would be guesswork by comparison, because nothing
/// reports that either way.
/// </para>
/// <para>
/// Every failure — no bus, a timeout, an exception — is treated as "no tray". Failing open to a
/// visible window is recoverable; failing closed to an invisible process is what this exists to
/// prevent.
/// </para>
/// </remarks>
internal static class TrayAvailability
{
    private const string WatcherName = "org.kde.StatusNotifierWatcher";

    /// <summary>How long to wait for the bus before assuming there is no tray.</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public static bool TrayCanBeShown()
    {
        // Only Linux uses StatusNotifierItem; elsewhere the platform tray is assumed present.
        if (!OperatingSystem.IsLinux()) return true;

        try
        {
            var probe = Task.Run(HasWatcherAsync);
            return probe.Wait(Timeout) && probe.Result;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Builds the NameHasOwner call. Separate and synchronous because
    /// <see cref="MessageWriter"/> is a ref struct and cannot cross an await.
    /// </summary>
    private static MessageBuffer CreateProbeMessage(Connection connection)
    {
        using var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.freedesktop.DBus",
            path: "/org/freedesktop/DBus",
            @interface: "org.freedesktop.DBus",
            member: "NameHasOwner",
            signature: "s");
        writer.WriteString(WatcherName);
        return writer.CreateMessage();
    }

    private static async Task<bool> HasWatcherAsync()
    {
        try
        {
            // Connection.Session is a cached singleton that Avalonia's own FreeDesktop
            // backend also uses. It is deliberately NOT disposed here: disposing it would
            // tear the tray out from under the app this check exists to protect.
            var connection = Connection.Session;
            await connection.ConnectAsync().ConfigureAwait(false);

            return await connection.CallMethodAsync(
                CreateProbeMessage(connection),
                static (Message reply, object? _) => reply.GetBodyReader().ReadBool(),
                null).ConfigureAwait(false);
        }
        catch
        {
            return false;
        }
    }
}
