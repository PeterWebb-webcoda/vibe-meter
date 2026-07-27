using System.Windows;
using VibeMeter.ViewModels;

namespace VibeMeter.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += (_, _) => RefreshGoogleAccountsList();
    }

    private void SaveClose_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
        {
            vm.Save();
        }
        Close();
    }

    /// <summary>
    /// Runs the interactive Google OAuth flow. On success the new account is persisted and
    /// the list refreshed; on failure the error is shown inline beneath the button.
    /// </summary>
    private async void AddGoogleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;

        AddGoogleAccountButton.IsEnabled = false;
        GoogleAccountStatus.Text = "Waiting for Google sign-in...";
        GoogleAccountStatus.Foreground = System.Windows.Media.Brushes.LightGray;

        try
        {
            var (email, error) = await vm.AddGoogleAccountAsync();
            if (string.IsNullOrEmpty(error))
            {
                GoogleAccountStatus.Text = $"Added {email}.";
                GoogleAccountStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                RefreshGoogleAccountsList();
            }
            else
            {
                GoogleAccountStatus.Text = $"Failed: {error}";
                GoogleAccountStatus.Foreground = System.Windows.Media.Brushes.LightCoral;
            }
        }
        finally
        {
            AddGoogleAccountButton.IsEnabled = true;
        }
    }

    /// <summary>Removes the account whose email is in the button's Tag.</summary>
    private void RemoveGoogleAccount_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        if (sender is not FrameworkElement { Tag: string email }) return;

        vm.RemoveGoogleAccount(email);
        GoogleAccountStatus.Text = $"Removed {email}.";
        GoogleAccountStatus.Foreground = System.Windows.Media.Brushes.LightGray;
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
