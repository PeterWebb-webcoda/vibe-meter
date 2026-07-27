using System;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
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
        // When the display layout changes (monitor connected/disconnected, DPI change,
        // resolution change), re-clamp into the visible work area so the window doesn't
        // end up on a now-disconnected screen. We deliberately do NOT clamp on every
        // LocationChanged — that would prevent the user dragging across monitors.
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.Invoke(ClampToWorkArea);
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
        // ActualWidth/Height are 0 before the first layout pass — fall back to the
        // designed dimensions so the window doesn't land at the very corner.
        double w = ActualWidth > 0 ? ActualWidth : (CompactMode ? 220 : FullWidth);
        double h = ActualHeight > 0 ? ActualHeight : (CompactMode ? 360 : FullHeight);
        Left = workArea.Right - w - 18;
        Top = workArea.Bottom - h - 18;
    }

    /// <summary>
    /// Nudges the window back inside the nearest monitor's work area if any edge has
    /// slipped off-screen (e.g. an external monitor was disconnected while the window
    /// was on it). No-op when already on-screen.
    /// </summary>
    private void ClampToWorkArea()
    {
        var workArea = SystemParameters.WorkArea;
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        if (w <= 0 || h <= 0) return;

        double newLeft = Left;
        double newTop = Top;

        // Off the right or left edge → pull onto the nearest horizontal edge.
        if (newLeft + w < workArea.Left + 40) newLeft = workArea.Left + 18;
        else if (newLeft > workArea.Right - 40) newLeft = workArea.Right - w - 18;

        // Off the bottom or top edge → pull onto the nearest vertical edge.
        if (newTop + h < workArea.Top + 40) newTop = workArea.Top + 18;
        else if (newTop > workArea.Bottom - 40) newTop = workArea.Bottom - h - 18;

        if (Math.Abs(newLeft - Left) > 0.5 || Math.Abs(newTop - Top) > 0.5)
        {
            Left = newLeft;
            Top = newTop;
        }
    }

    private bool CompactMode => _viewModel.CompactMode;

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

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProviderViewModel vm }) return;
        if (vm.ExtensionData is null) return;

        Window? window = vm.Id switch
        {
            "claude" => new CostDetailsWindow(vm.ExtensionData),
            "codex"  => new CodexCostDetailsWindow(vm.ExtensionData),
            _ => null
        };

        if (window is not null)
        {
            window.Owner = this;
            window.ShowDialog();
        }
    }

    /// <summary>
    /// Advances the Google card to the next account in the carousel. The provider handles
    /// the actual index bump + raise of ActiveAccountChanged, which the MainViewModel
    /// subscribed to in order to trigger a re-fetch.
    /// </summary>
    private void NextAccountButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.CycleGoogleAccount();

    /// <summary>Steps the Google card back to the previous account in the carousel.</summary>
    private void PrevAccountButton_Click(object sender, RoutedEventArgs e)
        => _viewModel.CycleGoogleAccountBack();
}
