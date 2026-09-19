using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibeMeter.Ui.Services;
using VibeMeter.Ui.ViewModels;

namespace VibeMeter.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    /// <summary>
    /// Design-time only. The XAML previewer instantiates a window through the runtime
    /// loader, which needs a public parameterless constructor; without one it cannot show
    /// either window (AVLN3001). It only loads the XAML - the real constructor is the one
    /// below, and nothing at runtime calls this.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// True when there is no system tray, so this window is the only way into the app
    /// and closing it must exit rather than hide it somewhere unreachable.
    /// </summary>
    public bool CloseExitsApp { get; private set; }

    /// <summary>
    /// Close hides to the tray instead of exiting (the app lives in the tray);
    /// the real close is only allowed while the App is quitting, or when there is no
    /// tray to hide into.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!CloseExitsApp && Application.Current is not App { IsQuitting: true })
        {
            e.Cancel = true;
            HideToTray();
        }
        base.OnClosing(e);
    }

    private void HideButton_Click(object? sender, RoutedEventArgs e) => HideToTray();

    /// <summary>
    /// Hides via the App so the Settings window goes with it. Hiding this window alone
    /// left Settings on screen with no parent, keeping it and its view model alive.
    /// </summary>
    private void HideToTray()
    {
        if (Application.Current is App app) app.HideMainWindow();
        else Hide();
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
        => (Application.Current as App)?.ShowSettings();

    /// <summary>
    /// Copies the error message from the clicked provider card to the clipboard,
    /// formatted with the provider name and the error log path — as the WPF
    /// app does.
    /// </summary>
    private async void CopyErrorButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ProviderViewModel vm }) return;

        var message =
            $"[{vm.DisplayName}] {vm.ErrorMessage}\n\n" +
            $"Logged to: {ErrorLog.LogPath}";
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(message);
        }
        catch
        {
            // Clipboard can be unavailable — ignore silently.
        }
    }

    /// <summary>
    /// Advances the Google card to the next account in the carousel. The
    /// provider handles the index bump and raises ActiveAccountChanged, which
    /// MainViewModel subscribed to in order to trigger a re-fetch.
    /// </summary>
    private void NextAccountButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.CycleGoogleAccount();

    /// <summary>Steps the Google card back to the previous account.</summary>
    private void PrevAccountButton_Click(object? sender, RoutedEventArgs e)
        => _viewModel.CycleGoogleAccountBack();

    /// <summary>
    /// Explains, in the window itself, that the window is only open because this desktop
    /// has no system tray to put the icon in.
    /// </summary>
    public void ShowTrayUnavailableNotice()
    {
        CloseExitsApp = true;
        TrayNoticeText.Text =
            "No system tray was found on this desktop, so Vibe Meter is showing this window " +
            "instead of a tray icon. GNOME needs the AppIndicator extension; Cinnamon, KDE " +
            "and XFCE work as-is. Closing this window exits the app.";
        TrayNotice.IsVisible = true;
    }
}
