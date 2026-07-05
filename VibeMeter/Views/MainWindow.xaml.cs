using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using VibeMeter.Services;
using VibeMeter.ViewModels;

namespace VibeMeter.Views;

public partial class MainWindow : Window
{
    private const double FullWidth = 420;
    private const double FullHeight = 780;

    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;

        ApplyCompactLayout(_viewModel.CompactMode);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyCompactLayout(_viewModel.CompactMode);
        ResetPosition();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CompactMode))
        {
            ApplyCompactLayout(_viewModel.CompactMode);
        }
    }

    private void ApplyCompactLayout(bool compact)
    {
        ClearValue(WidthProperty);
        ClearValue(HeightProperty);
        ClearValue(MinWidthProperty);
        ClearValue(MinHeightProperty);

        if (compact)
        {
            ResizeMode = ResizeMode.NoResize;
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            MinWidth = 220;
            MinHeight = 360;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            Width = FullWidth;
            Height = FullHeight;
        }

        if (IsLoaded)
            ResetPosition();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    public void ResetPosition()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - ActualWidth - 18;
        Top = workArea.Bottom - ActualHeight - 18;
    }

    private void ResetPositionButton_Click(object sender, RoutedEventArgs e) => ResetPosition();

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
        => (System.Windows.Application.Current as App)?.ShowSettings();

    /// <summary>
    /// Copies the error message from the clicked provider card to the clipboard, formatted
    /// with the provider name and a reference to the error log file.
    /// </summary>
    private void CopyErrorButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProviderViewModel vm }) return;

        var message =
            $"[{vm.DisplayName}] {vm.ErrorMessage}\n\n" +
            $"Logged to: {ErrorLog.LogPath}";
        try
        {
            Clipboard.SetText(message);
        }
        catch
        {
            // Clipboard can be locked by other processes — ignore silently.
        }
    }
}
