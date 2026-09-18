using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Hardcodet.Wpf.TaskbarNotification;
using VibeMeter.Services;
using VibeMeter.ViewModels;
using VibeMeter.Views;

namespace VibeMeter;

public partial class App : Application
{
    private TaskbarIcon? _notifyIcon;
    private Views.MainWindow? _mainWindow;
    private SettingsWindow? _settingsWindow;
    private MainViewModel? _mainViewModel;
    private DispatcherTimer? _refreshTimer;
    private SettingsService? _settingsService;
    private ProviderRegistry? _registry;

    /// <summary>
    /// Publishing to the collection API, or null when the user has not opted in
    /// (the default). Owned here rather than by the view model because the
    /// sign-in prompt needs the tray icon, and because it must be disposed —
    /// which is what cancels an in-flight publish at exit.
    /// </summary>
    private VibeMeter.Publishing.DesktopPublishHost? _publishHost;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Services
        _settingsService = new SettingsService();

        // The Google provider is given THIS host's account source, so the
        // reading it publishes is built from the same settings file the card
        // shows. See SettingsGoogleAccountSource.
        _registry = new ProviderRegistry(new SettingsGoogleAccountSource(_settingsService));

        // ViewModels
        _mainViewModel = new MainViewModel(_registry, _settingsService);

        // MainWindow
        _mainWindow = new Views.MainWindow(_mainViewModel);

        // Tray icon
        _notifyIcon = new TaskbarIcon
        {
            Icon = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/icon.ico")).Stream),
            ToolTipText = "Vibe Meter",
            LeftClickCommand = new RelayCommand(ToggleMainWindow)
        };

        var contextMenu = new ContextMenu();

        var refreshItem = new MenuItem { Header = "Refresh Now" };
        refreshItem.Click += async (s, args) =>
        {
            if (_mainViewModel != null) await _mainViewModel.RefreshAsync();
        };

        var toggleItem = new MenuItem { Header = "Show/Hide" };
        toggleItem.Click += (s, args) => ToggleMainWindow();

        var resetPosItem = new MenuItem { Header = "Reset Position" };
        resetPosItem.Click += (s, args) => _mainWindow.ResetPosition();

        // Compact mode mirrors (and persists) MainViewModel.CompactMode.
        var compactItem = new MenuItem { Header = "Compact mode", IsCheckable = true, IsChecked = _mainViewModel.CompactMode };
        compactItem.Checked += (s, args) =>
        {
            _mainViewModel.CompactMode = true;
            _mainViewModel.SaveSettings();
        };
        compactItem.Unchecked += (s, args) =>
        {
            _mainViewModel.CompactMode = false;
            _mainViewModel.SaveSettings();
        };
        _mainViewModel.PropertyChanged += (s, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.CompactMode))
                compactItem.IsChecked = _mainViewModel.CompactMode;
        };

        var settingsItem = new MenuItem { Header = "Settings" };
        settingsItem.Click += (s, args) => ShowSettings();

        var quitItem = new MenuItem { Header = "Quit" };
        quitItem.Click += (s, args) => Shutdown();

        contextMenu.Items.Add(refreshItem);
        contextMenu.Items.Add(toggleItem);
        contextMenu.Items.Add(resetPosItem);
        contextMenu.Items.Add(compactItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(settingsItem);
        contextMenu.Items.Add(new Separator());
        contextMenu.Items.Add(quitItem);

        _notifyIcon.ContextMenu = contextMenu;

        // Auto-refresh timer
        _refreshTimer = new DispatcherTimer();
        _refreshTimer.Tick += async (s, args) =>
        {
            if (_mainViewModel != null && _mainViewModel.AutoRefreshEnabled)
            {
                await _mainViewModel.RefreshAsync();
            }
            if (_mainViewModel != null)
            {
                _refreshTimer.Interval = TimeSpan.FromSeconds(_mainViewModel.RefreshIntervalSeconds);
            }
        };
        _refreshTimer.Interval = TimeSpan.FromSeconds(_mainViewModel.RefreshIntervalSeconds);
        _refreshTimer.Start();

        // Opt-in publishing. Built after the tray icon, which is where the
        // device-code prompt is shown; this only reads settings and (when
        // switched on) creates a queue directory, so it never delays startup and
        // never asks anyone to sign in.
        RestartPublishing();

        // Initial refresh + show
        _ = _mainViewModel.RefreshAsync();
        _mainWindow.Show();
    }

    /// <summary>
    /// (Re)builds the publish host from the saved settings. Called at startup
    /// and again after the settings are saved, so ticking the opt-in takes
    /// effect immediately rather than at the next launch.
    /// </summary>
    private void RestartPublishing()
    {
        if (_settingsService == null || _mainViewModel == null) return;

        // Disposing cancels anything in flight; it is bounded so it cannot hang
        // the UI thread. Anything unsent is already on disk in the offline
        // queue, and the replacement host flushes it on its first publish.
        _publishHost?.Dispose();
        _publishHost = TrayPublishing.TryStart(_settingsService.Load(), ShowSignInPrompt);
        _mainViewModel.UsePublishHost(_publishHost);
    }

    /// <summary>
    /// Shows the device-code sign-in message where the user will actually see
    /// it: a tray balloon, plus a line in the error log because a balloon
    /// disappears and the code is valid for several minutes longer than that.
    /// </summary>
    /// <remarks>
    /// The message arrives on the publishing thread, so the balloon is
    /// marshalled to the dispatcher. It carries a verification URL and a user
    /// code and never a token, so it is safe to display and to log.
    /// </remarks>
    private void ShowSignInPrompt(string message)
    {
        ErrorLog.Write(ErrorLogPublishLog.Source, "Publishing", message);

        Dispatcher.BeginInvoke(() =>
        {
            _notifyIcon?.ShowBalloonTip("Vibe Meter: sign in to publish", message, BalloonIcon.Info);
        });
    }

    private void ToggleMainWindow()
    {
        if (_mainWindow == null) return;

        if (_mainWindow.IsVisible)
        {
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
                _mainWindow.Activate();
            }
            else
            {
                _mainWindow.Hide();
            }
        }
        else
        {
            _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
            {
                _mainWindow.WindowState = WindowState.Normal;
            }
            _mainWindow.Activate();
        }
    }

    public void ShowSettings()
    {
        if (_settingsWindow != null && _settingsWindow.IsLoaded)
        {
            _settingsWindow.Activate();
            return;
        }

        if (_mainViewModel == null || _settingsService == null) return;

        var settingsViewModel = new SettingsViewModel(_mainViewModel, _settingsService, RestartPublishing);
        _settingsWindow = new SettingsWindow(settingsViewModel);
        _settingsWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Cancels any publish still in flight (bounded — see DesktopPublishHost.Dispose)
        // so the process is not held open by an HTTP attempt nobody is waiting for.
        _publishHost?.Dispose();
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }
}

/// <summary>Minimal ICommand used by the tray icon's LeftClickCommand.</summary>
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    public RelayCommand(Action execute) => _execute = execute;
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _execute();
}
