using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibeMeter.Ui.Services;
using VibeMeter.Ui.ViewModels;

namespace VibeMeter.Avalonia.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>
    /// Close hides to the tray instead of exiting (the app lives in the tray);
    /// the real close is only allowed while the App is quitting.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Application.Current is not App { IsQuitting: true })
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    private void HideButton_Click(object? sender, RoutedEventArgs e) => Hide();

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
}
