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
    private readonly Action? _onSaved;

    [ObservableProperty] private bool _autoRefreshEnabled;
    [ObservableProperty] private int _refreshIntervalSeconds;
    [ObservableProperty] private MeterStyle _meterStyle;
    [ObservableProperty] private bool _alwaysOnTop;
    [ObservableProperty] private bool _launchAtStartup;
    [ObservableProperty] private string _lastUpdatedText = "Not updated yet";

    // --- Publishing to the collection API (opt-in, off by default) ---

    [ObservableProperty] private bool _publishEnabled;
    [ObservableProperty] private string _publishApiBaseUrl = "";
    [ObservableProperty] private string _publishClientId = "";
    [ObservableProperty] private string _publishTenantId = "";
    [ObservableProperty] private string _publishScope = "";

    /// <summary>
    /// What publishing will do with the values as saved. Shown under the
    /// section, because a half-filled configuration otherwise fails silently on
    /// a background thread where nobody would look for it.
    /// </summary>
    [ObservableProperty] private string _publishStatusText = "";

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

    /// <param name="onSaved">
    /// Run after <see cref="Save"/> has written the file. The app uses it to
    /// rebuild publishing, so switching the opt-in on or off takes effect
    /// straight away instead of at the next launch.
    /// </param>
    public SettingsViewModel(MainViewModel mainViewModel, SettingsService settingsService, Action? onSaved = null)
    {
        _mainViewModel = mainViewModel;
        _settingsService = settingsService;
        _onSaved = onSaved;
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

        PublishEnabled = settings.PublishEnabled;
        PublishApiBaseUrl = settings.PublishApiBaseUrl;
        PublishClientId = settings.PublishClientId;
        PublishTenantId = settings.PublishTenantId;
        PublishScope = settings.PublishScope;
        UpdatePublishStatusText();

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

    /// <summary>
    /// The configured Google accounts, for the Settings UI list. Projected
    /// rather than bound directly, so the window never holds a
    /// <see cref="VibeMeter.Providers.Google.GoogleAccount"/> - whose in-memory
    /// refresh token has no business being reachable from a data template - and
    /// so an account whose stored credential could not be opened on this
    /// machine says so instead of silently failing at the next refresh.
    /// </summary>
    public List<GoogleAccountRow> GetGoogleAccounts()
    {
        var rows = new List<GoogleAccountRow>();
        foreach (var account in _mainViewModel.GetGoogleAccounts())
        {
            rows.Add(new GoogleAccountRow(
                account.Email,
                account.NeedsReauthentication
                    ? "credential unavailable on this profile - remove and add again"
                    : ""));
        }
        return rows;
    }

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

        settings.PublishEnabled = PublishEnabled;
        settings.PublishApiBaseUrl = PublishApiBaseUrl?.Trim() ?? "";
        settings.PublishClientId = PublishClientId?.Trim() ?? "";
        settings.PublishTenantId = PublishTenantId?.Trim() ?? "";
        settings.PublishScope = PublishScope?.Trim() ?? "";

        foreach (var toggle in ProviderToggles)
        {
            settings.ProviderEnabled[toggle.Id] = toggle.IsEnabled;
        }

        _settingsService.Save(settings);
        UpdateStartupShortcut();
        UpdatePublishStatusText();

        // Rebuilds publishing against what was just written.
        _onSaved?.Invoke();
    }

    /// <summary>
    /// Describes what publishing will do, using the same validation the host
    /// itself applies, so the window and the background thread can never
    /// disagree about whether the settings are usable.
    /// </summary>
    private void UpdatePublishStatusText()
    {
        if (!PublishEnabled)
        {
            PublishStatusText = "Off — nothing leaves this machine.";
            return;
        }

        var candidate = new VibeMeter.Publishing.DesktopPublishSettings(
            PublishEnabled, PublishApiBaseUrl, PublishClientId, PublishTenantId, PublishScope);

        PublishStatusText = candidate.TryResolve(out _, out var problems)
            ? "On — each refresh publishes this machine's usage. You will be asked to sign in on the first publish."
            : "Not publishing yet: " + string.Join("; ", problems) + ".";
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

    /// <summary>
    /// Registers or removes exactly one launcher in the per-user Startup folder.
    /// </summary>
    /// <remarks>
    /// <para>The app has no single-instance guard, so leaving two launchers behind would put
    /// two tray icons up at logon. A hand-made <c>VibeMeter.lnk</c> is therefore treated as
    /// the launcher when one is present — it is the tidier of the two (a <c>.bat</c> flashes
    /// a console window at logon) — and no <c>.bat</c> is added alongside it.</para>
    /// <para>Disabling removes <em>both</em> forms, otherwise the toggle would appear to do
    /// nothing whenever a shortcut was the active launcher.</para>
    /// </remarks>
    private void UpdateStartupShortcut()
    {
        try
        {
            string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string batPath = System.IO.Path.Combine(startupFolder, "VibeMeter.bat");
            string lnkPath = System.IO.Path.Combine(startupFolder, "VibeMeter.lnk");

            if (LaunchAtStartup)
            {
                // An existing shortcut already launches us — leave it as the sole launcher.
                if (System.IO.File.Exists(lnkPath))
                {
                    if (System.IO.File.Exists(batPath))
                        System.IO.File.Delete(batPath);
                    return;
                }

                string exePath = Environment.ProcessPath
                    ?? System.IO.Path.Combine(System.AppContext.BaseDirectory, "VibeMeter.exe");
                System.IO.File.WriteAllText(batPath, $"@echo off\nstart \"\" \"{exePath}\"");
            }
            else
            {
                if (System.IO.File.Exists(batPath))
                    System.IO.File.Delete(batPath);
                if (System.IO.File.Exists(lnkPath))
                    System.IO.File.Delete(lnkPath);
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

/// <summary>One row of the Settings window's Google account list.</summary>
/// <param name="Note">Empty unless the account needs attention.</param>
public record GoogleAccountRow(string Email, string Note)
{
    public bool HasNote => Note.Length > 0;
}

public record RefreshIntervalOption(int Seconds, string Label);
public record MeterStyleOption(MeterStyle Style, string Label);
