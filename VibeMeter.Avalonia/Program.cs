using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using VibeMeter.Ui.Services;

namespace VibeMeter.Avalonia;

internal static class Program
{
    /// <summary>
    /// Held for the lifetime of the process. Releasing it is what lets the next launch start,
    /// so it is deliberately never disposed before exit.
    /// </summary>
    private static FileStream? _instanceLock;

    [STAThread]
    public static void Main(string[] args)
    {
        // Before Avalonia, so a second launch costs nothing and touches no provider API.
        if (!TryTakeInstanceLock())
        {
            // Launching again is what someone does when they cannot find the window, so
            // that is treated as "show it", not as an error. stderr alone would go nowhere
            // from a .desktop launcher.
            RequestShowFromRunningInstance();
            Console.Error.WriteLine("Vibe Meter is already running; asked it to show its window.");
            return;
        }

        InstallLastResortHandlers();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }

    /// <summary>
    /// Takes an exclusive lock so only one copy runs at a time.
    /// </summary>
    /// <remarks>
    /// Without this, every launch added another process. That is bad anywhere, and worse
    /// here: the app normally has no window and no taskbar entry, so the extra copies are
    /// invisible, and each one runs its own refresh timer against the provider APIs. The
    /// lock is a file opened with <see cref="FileShare.None"/> — the handle closing at exit,
    /// however the process ends, is what releases it, so a crash cannot leave it stuck.
    /// </remarks>
    private static bool TryTakeInstanceLock()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeMeter");
            Directory.CreateDirectory(dir);

            _instanceLock = new FileStream(
                Path.Combine(dir, "desktop.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            // Held by another instance.
            return false;
        }
        catch (Exception)
        {
            // Anything else (an unwritable directory, say) must not stop the app starting.
            // Failing open risks a second copy; failing closed would refuse to run at all.
            return true;
        }
    }

    /// <summary>The marker a second launch drops for the running instance to notice.</summary>
    public static string ShowRequestPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VibeMeter", "show-request");

    private static void RequestShowFromRunningInstance()
    {
        try { File.WriteAllText(ShowRequestPath, DateTime.UtcNow.ToString("O")); }
        catch { /* the running instance simply will not hear about it */ }
    }

    /// <summary>
    /// Records exceptions that escape every other handler.
    /// </summary>
    /// <remarks>
    /// Several UI paths are <c>async void</c>, and an exception escaping one of those ends
    /// the process with no message at all. On an app that normally shows only a tray icon,
    /// that looks like the icon simply vanishing. This cannot keep the process alive, but it
    /// makes the reason recoverable from the error log instead of leaving nothing behind.
    /// </remarks>
    private static void InstallLastResortHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            SafeLog("unhandled", (e.ExceptionObject as Exception)?.ToString() ?? "unknown error");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            SafeLog("unobserved-task", e.Exception.ToString());
            e.SetObserved();
        };
    }

    private static void SafeLog(string kind, string message)
    {
        try { ErrorLog.Write("app", "Vibe Meter (" + kind + ")", message); }
        catch { /* the handler of last resort cannot itself throw */ }
    }

    // OnExplicitShutdown: the app lives in the tray, so closing/hiding the
    // window must never exit the process - only the tray Quit item may.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
