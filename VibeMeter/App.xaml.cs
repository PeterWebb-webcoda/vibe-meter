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

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Services
        _settingsService = new SettingsService();
        _registry = new ProviderRegistry();

        // ViewModels
        _mainViewModel = new MainViewModel(_registry, _settingsService);

        // MainWindow
        _mainWindow = new Views.MainWindow(_mainViewModel);

        // Tray icon
        _notifyIcon = new TaskbarIcon
        {
            Icon = System.Drawing.SystemIcons.Information,
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

        // Initial refresh + show
        _ = _mainViewModel.RefreshAsync();
        _mainWindow.Show();
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

        var settingsViewModel = new SettingsViewModel(_mainViewModel, _settingsService);
        _settingsWindow = new SettingsWindow(settingsViewModel);
        _settingsWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
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
