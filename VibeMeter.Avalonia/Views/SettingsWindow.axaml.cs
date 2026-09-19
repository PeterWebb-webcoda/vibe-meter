using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using VibeMeter.Ui.Models;
using VibeMeter.Ui.ViewModels;

namespace VibeMeter.Avalonia.Views;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel? _mainViewModel;
    private bool _loading = true;

    public SettingsWindow(SettingsViewModel viewModel, MainViewModel mainViewModel)
    {
        InitializeComponent();
        _mainViewModel = mainViewModel;
        DataContext = viewModel;

        // Tint: SettingsViewModel deliberately has no tint surface (the WPF app
        // cycles tint from the main window), so the window writes the choice
        // straight to MainViewModel.TintIndex and persists with SaveSettings —
        // the same persistence path CycleTint uses.
        TintCombo.ItemsSource = WidgetTint.All;
        TintCombo.SelectedIndex = mainViewModel.TintIndex % WidgetTint.All.Count;

        // Avalonia ComboBox has no SelectedValuePath, so the option <-> value
        // mapping for these two is done here at the rendering edge.
        RefreshIntervalCombo.ItemsSource = viewModel.RefreshIntervals;
        RefreshIntervalCombo.SelectedItem = viewModel.RefreshIntervals
            .FirstOrDefault(o => o.Seconds == viewModel.RefreshIntervalSeconds);
        MeterStyleCombo.ItemsSource = viewModel.MeterStyles;
        MeterStyleCombo.SelectedItem = viewModel.MeterStyles
            .FirstOrDefault(o => o.Style == viewModel.MeterStyle);

        _loading = false;

        RefreshGoogleAccountsList();
    }

    private void TintCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || _mainViewModel is null) return;
        if (TintCombo.SelectedIndex < 0) return;

        _mainViewModel.TintIndex = TintCombo.SelectedIndex;
        _mainViewModel.SaveSettings();
    }

    private void RefreshIntervalCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (RefreshIntervalCombo.SelectedItem is RefreshIntervalOption option)
            vm.RefreshIntervalSeconds = option.Seconds;
    }

    private void MeterStyleCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        if (MeterStyleCombo.SelectedItem is MeterStyleOption option)
            vm.MeterStyle = option.Style;
    }

    private void SaveClose_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as SettingsViewModel)?.Save();
        Close();
    }

    /// <summary>
    /// Runs the interactive Google OAuth flow. On success the new account is
    /// persisted and the list refreshed; on failure the error is shown inline
    /// beneath the button.
    /// </summary>
    private async void AddGoogleAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        AddGoogleAccountButton.IsEnabled = false;
        GoogleAccountStatus.Text = "Waiting for Google sign-in...";

        try
        {
            var (email, error) = await vm.AddGoogleAccountAsync();
            if (string.IsNullOrEmpty(error))
            {
                GoogleAccountStatus.Text = $"Added {email}.";
                RefreshGoogleAccountsList();
            }
            else
            {
                GoogleAccountStatus.Text = $"Failed: {error}";
            }
        }
        finally
        {
            AddGoogleAccountButton.IsEnabled = true;
        }
    }

    /// <summary>Removes the account whose email is in the button's Tag.</summary>
    private void RemoveGoogleAccount_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (sender is not Control { Tag: string email }) return;

        vm.RemoveGoogleAccount(email);
        GoogleAccountStatus.Text = $"Removed {email}.";
        RefreshGoogleAccountsList();
    }

    private void RefreshGoogleAccountsList()
    {
        if (DataContext is SettingsViewModel vm)
        {
            GoogleAccountsList.ItemsSource = vm.GetGoogleAccounts();
        }
    }
}
