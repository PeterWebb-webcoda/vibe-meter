using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using VibeMeter.Avalonia.Views;
using VibeMeter.Ui.Services;
using VibeMeter.Ui.ViewModels;

namespace VibeMeter.Avalonia;

public class App : Application
{
    private TrayIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;

    /// <summary>False when this desktop hosts no system tray, so the window stands in for it.</summary>
    private bool _trayAvailable = true;
    private MainViewModel? _mainViewModel;
    private DispatcherTimer? _refreshTimer;
    private SettingsService? _settingsService;

    /// <summary>
    /// Publishing to the collection API, or null when the user has not opted in
    /// (the default). Owned here rather than by the view model because the
    /// sign-in prompt needs the tray, and because it must be disposed — which
    /// is what cancels an in-flight publish at exit. Mirrors the WPF App.
    /// </summary>
    private VibeMeter.Publishing.DesktopPublishHost? _publishHost;

    private static WindowIcon? _appIcon;

    /// <summary>Set before Shutdown so the main window's close-to-tray
    /// behaviour stands aside and lets the real close through.</summary>
    public bool IsQuitting { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Exit += OnLifetimeExit;

            var settingsService = new SettingsService();
            _settingsService = settingsService;

            // Same wiring as the WPF App: the Google provider reads THIS host's
            // configured accounts from the shared settings file.
            var registry = new ProviderRegistry(new SettingsGoogleAccountSource(settingsService));

            // Constructed on the UI thread so MainViewModel captures the
            // Avalonia SynchronizationContext for marshalling card updates.
            _mainViewModel = new MainViewModel(registry, settingsService);

            _mainWindow = new MainWindow(_mainViewModel)
            {
                Icon = AppIcon
            };

            // Whether the tray can actually show anything decides how this app behaves.
            // With no tray AND no window AND ShowInTaskbar="False", the process would have
            // no reachable UI at all - see TrayAvailability.
            _trayAvailable = TrayAvailability.TrayCanBeShown();
            if (_trayAvailable)
            {
                BuildTrayIcon();
            }

            // Auto-refresh timer - identical policy to the WPF app.
            _refreshTimer = new DispatcherTimer(
                TimeSpan.FromSeconds(_mainViewModel.RefreshIntervalSeconds),
                DispatcherPriority.Background,
                async (_, _) =>
                {
                    if (_mainViewModel is { } vm && vm.AutoRefreshEnabled)
                        await vm.RefreshAsync();
                    if (_refreshTimer is not null && _mainViewModel is not null)
                        _refreshTimer.Interval = TimeSpan.FromSeconds(_mainViewModel.RefreshIntervalSeconds);
                });
            _refreshTimer.Start();

            // Opt-in publishing (no-op unless enabled in settings).
            RestartPublishing();

            if (!_trayAvailable)
            {
                // No tray to minimise to, so the window is the only way in - and closing it
                // has to end the process, because OnExplicitShutdown means nothing else will.
                // ShowTrayUnavailableNotice sets CloseExitsApp, so OnClosing lets the close
                // through and this Closed handler actually runs. Without it the window would
                // cancel its own close and hide into a tray that does not exist.
                _mainWindow.ShowTrayUnavailableNotice();
                _mainWindow.Closed += (_, _) => Quit();
                ShowMainWindow();
            }

            // Initial refresh. With a tray, the app starts minimised to it: the window is
            // deliberately NOT shown here - the tray menu / tray click does it.
            _ = _mainViewModel.RefreshAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static WindowIcon AppIcon =>
        _appIcon ??= new WindowIcon(AssetLoader.Open(
            new Uri("avares://VibeMeter.Avalonia/Assets/icon.png")));

    // --- Tray ---

    private void BuildTrayIcon()
    {
        var menu = new NativeMenu();

        var refreshItem = new NativeMenuItem { Header = "Refresh" };
        refreshItem.Click += async (_, _) =>
        {
            if (_mainViewModel is not null) await _mainViewModel.RefreshAsync();
        };

        var showItem = new NativeMenuItem { Header = "Show" };
        showItem.Click += (_, _) => ShowMainWindow();

        var settingsItem = new NativeMenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => ShowSettings();

        var quitItem = new NativeMenuItem { Header = "Quit" };
        quitItem.Click += (_, _) => Quit();

        menu.Add(refreshItem);
        menu.Add(showItem);
        menu.Add(settingsItem);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(quitItem);

        _trayIcon = new TrayIcon
        {
            Icon = AppIcon,
            ToolTipText = "Vibe Meter",
            Menu = menu
        };
        _trayIcon.Clicked += (_, _) => ToggleMainWindow();

        // TrayIcon.Icons is an attached property on the Application (usually
        // set from App.axaml); this is the code equivalent.
        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private void ToggleMainWindow()
    {
        if (_mainWindow is null) return;

        // A minimised window still reports IsVisible == true, so testing visibility alone
        // made a tray click on a minimised window HIDE it and need a second click to bring
        // it back. Minimised counts as "not really on screen", same as ShowMainWindow treats it.
        if (_mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized)
            HideMainWindow();
        else
            ShowMainWindow();
    }

    /// <summary>
    /// Hides the main window, taking any open Settings window with it.
    /// </summary>
    /// <remarks>
    /// Hiding the main window on its own left Settings on screen owned by a window that
    /// was no longer there: unsaved edits were unreachable, and the window plus its view
    /// model stayed rooted in the lifetime's window list.
    /// </remarks>
    public void HideMainWindow()
    {
        if (_settingsWindow is { } settings)
        {
            settings.Close();
            _settingsWindow = null;
        }

        _mainWindow?.Hide();
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    public void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        if (_mainViewModel is null || _settingsService is null) return;

        var settingsViewModel = new SettingsViewModel(_mainViewModel, _settingsService, RestartPublishing);
        _settingsWindow = new SettingsWindow(settingsViewModel, _mainViewModel)
        {
            Icon = AppIcon,
            // The main window is pinned above everything while AlwaysOnTop is set.
            // A settings window that does not match it opens underneath, which reads
            // as the Settings menu item having done nothing at all.
            Topmost = _mainWindow?.Topmost ?? false
        };

        // Owning it to the main window keeps it above that window specifically, and
        // lets the two minimise and restore together. The owner has to be on screen
        // for that, and it is not when Settings is opened straight from the tray.
        if (_mainWindow is { IsVisible: true })
            _settingsWindow.Show(_mainWindow);
        else
            _settingsWindow.Show();
    }

    // --- Publishing (mirrors the WPF App's RestartPublishing / ShowSignInPrompt) ---

    private void RestartPublishing()
    {
        if (_settingsService is null || _mainViewModel is null) return;

        _publishHost?.Dispose();
        _publishHost = TrayPublishing.TryStart(_settingsService.Load(), ShowSignInPrompt);
        _mainViewModel.UsePublishHost(_publishHost);
    }

    /// <summary>
    /// Avalonia 11's TrayIcon has no balloon API, so the device-code message is
    /// logged to the error log (as the WPF app also does) and flagged in the
    /// main window's status line instead of being shown as a balloon.
    /// </summary>
    private void ShowSignInPrompt(string message)
    {
        ErrorLog.Write(ErrorLogPublishLog.Source, "Publishing", message);

        Dispatcher.UIThread.Post(() =>
        {
            if (_mainViewModel is not null)
                _mainViewModel.StatusMessage = "Publish sign-in needed - see error log";
        });
    }

    // --- Exit ---

    private void Quit()
    {
        IsQuitting = true;
        _publishHost?.Dispose();
        _trayIcon?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            lifetime.Shutdown();
    }

    private void OnLifetimeExit(object? sender, EventArgs e)
    {
        // Cancels any publish still in flight (bounded — see
        // DesktopPublishHost.Dispose) and removes the tray icon.
        _publishHost?.Dispose();
        _trayIcon?.Dispose();
        TrayIcon.SetIcons(this, null);
    }
}
