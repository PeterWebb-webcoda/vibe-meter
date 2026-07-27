using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeMeter.Models;
using VibeMeter.Services;

namespace VibeMeter.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _mainViewModel;
    private readonly SettingsService _settingsService;

    [ObservableProperty] private bool _autoRefreshEnabled;
    [ObservableProperty] private int _refreshIntervalSeconds;
    [ObservableProperty] private MeterStyle _meterStyle;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private string _lastUpdatedText = "Not updated yet";

    public ObservableCollection<ProviderToggle> ProviderToggles { get; } = new();

    public List<RefreshIntervalOption> RefreshIntervals { get; } = new()
    {
        new(30, "30 seconds"),
        new(60, "1 minute"),
        new(120, "2 minutes"),
        new(300, "5 minutes")
    };

    public List<MeterStyleOption> MeterStyles { get; } = new()
    {
        new(MeterStyle.Circular, "Circular"),
        new(MeterStyle.Horizontal, "Bars"),
        new(MeterStyle.Battery, "Battery")
    };

    public SettingsViewModel(MainViewModel mainViewModel, SettingsService settingsService)
    {
        _mainViewModel = mainViewModel;
        _settingsService = settingsService;
        LoadFromMainViewModel();
    }

    private void LoadFromMainViewModel()
    {
        AutoRefreshEnabled = _mainViewModel.AutoRefreshEnabled;
        RefreshIntervalSeconds = _mainViewModel.RefreshIntervalSeconds;
        MeterStyle = _mainViewModel.MeterStyle;
        AlwaysOnTop = _mainViewModel.AlwaysOnTop;

        var settings = _settingsService.Load();
        LaunchAtStartup = settings.LaunchAtStartup;

        ProviderToggles.Clear();
        foreach (var card in _mainViewModel.Providers)
        {
            ProviderToggles.Add(new ProviderToggle(card.Id, card.DisplayName)
            {
                IsEnabled = settings.IsProviderEnabled(card.Id)
            });
        }

        UpdateLastUpdatedText();
    }

    /// <summary>The configured Google accounts, for the Settings UI list.</summary>
    public System.Collections.Generic.IReadOnlyList<VibeMeter.Providers.Google.GoogleAccount> GetGoogleAccounts()
        => _mainViewModel.GetGoogleAccounts();

    /// <summary>Runs the interactive Google OAuth flow; returns (email, error).</summary>
    public async Task<(string Email, string? Error)> AddGoogleAccountAsync()
    {
        var (email, error) = await _mainViewModel.AddGoogleAccountAsync();
        return (email, error);
    }

    /// <summary>Removes a Google account by email.</summary>
    public void RemoveGoogleAccount(string email)
        => _mainViewModel.RemoveGoogleAccount(email);

    [RelayCommand]
    public void Save()
    {
        _mainViewModel.AutoRefreshEnabled = AutoRefreshEnabled;
        _mainViewModel.RefreshIntervalSeconds = RefreshIntervalSeconds;
        _mainViewModel.MeterStyle = MeterStyle;
        _mainViewModel.AlwaysOnTop = AlwaysOnTop;
        _mainViewModel.SaveSettings();

        var settings = _settingsService.Load();
        settings.LaunchAtStartup = LaunchAtStartup;

        foreach (var toggle in ProviderToggles)
        {
            settings.ProviderEnabled[toggle.Id] = toggle.IsEnabled;
        }

        _settingsService.Save(settings);
        UpdateStartupShortcut();
    }

    [RelayCommand]
    public async System.Threading.Tasks.Task RefreshAsync()
    {
        await _mainViewModel.RefreshAsync();
        UpdateLastUpdatedText();
    }

    private void UpdateLastUpdatedText()
    {
        LastUpdatedText = _mainViewModel.LastUpdated.HasValue
            ? $"Updated {_mainViewModel.LastUpdated.Value:t}"
            : "Not updated yet";
    }

    private void UpdateStartupShortcut()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string batPath = System.IO.Path.Combine(startupFolder, "VibeMeter.bat");

            if (LaunchAtStartup)
            {
                string exePath = Environment.ProcessPath
                    ?? System.IO.Path.Combine(System.AppContext.BaseDirectory, "VibeMeter.exe");
                System.IO.File.WriteAllText(batPath, $"@echo off\nstart \"\" \"{exePath}\"");
            }
            else
            {
                if (System.IO.File.Exists(batPath))
                    System.IO.File.Delete(batPath);
            }
        }
        catch
        {
            // Silently ignore startup-shortcut errors.
        }
    }
}

public partial class ProviderToggle : ObservableObject
{
    public string Id { get; }
    public string DisplayName { get; }

    [ObservableProperty] private bool _isEnabled;

    public ProviderToggle(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }
}

public record RefreshIntervalOption(int Seconds, string Label);
public record MeterStyleOption(MeterStyle Style, string Label);
