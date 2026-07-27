using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeMeter.Core;
using VibeMeter.Models;
using VibeMeter.Providers.Google;
using VibeMeter.Services;

namespace VibeMeter.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProviderRegistry _registry;
    private readonly SettingsService _settingsService;
    private SettingsData _settings;

    public ObservableCollection<ProviderViewModel> Providers { get; } = new();

    // --- Observable properties ---

    [ObservableProperty] private string _statusMessage = "Ready";

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private DateTime? _lastUpdated;

    [ObservableProperty] private MeterStyle _meterStyle = MeterStyle.Circular;

    [ObservableProperty] private bool _autoRefreshEnabled = true;

    // 5 min default: usage data barely moves in 60s, and the cost calculators scan the
    // full transcript corpus on each refresh. User-configurable via Settings.
    [ObservableProperty] private int _refreshIntervalSeconds = 300;

    [ObservableProperty] private int _tintIndex;

    [ObservableProperty] private bool _alwaysOnTop = true;

    [ObservableProperty] private string _freshnessText = "";

    /// <summary>When true, the window renders as a slim horizontal strip instead of the full panel.</summary>
    [ObservableProperty] private bool _compactMode;

    /// <summary>App version label for the footer, e.g. "v0.3.0".</summary>
    public string VersionText { get; } = "v" + (System.Reflection.Assembly
        .GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0");

    public WidgetTint CurrentTint => WidgetTint.All[TintIndex % WidgetTint.All.Count];
    public SolidColorBrush TintPrimaryBrush => new(CurrentTint.Primary);
    public SolidColorBrush TintSecondaryBrush => new(CurrentTint.Secondary);
    public SolidColorBrush TintGlowBrush => new(CurrentTint.Glow);

    // --- Constructors ---

    public MainViewModel() : this(new ProviderRegistry(), new SettingsService()) { }

    public MainViewModel(ProviderRegistry registry, SettingsService settingsService)
    {
        _registry = registry;
        _settingsService = settingsService;
        _settings = _settingsService.Load();
        ApplySettings(_settings);

        // Seed the Google provider with VibeMeter-owned accounts from settings.
        SyncGoogleAccountsToProvider();

        // Re-fetch when the user cycles to the next Google account.
        GoogleProvider.ActiveAccountChanged += async () => await RefreshAsync();

        foreach (var provider in _registry.Providers)
        {
            Providers.Add(new ProviderViewModel(provider));
        }
    }

    // --- Commands ---

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        StatusMessage = "Refreshing...";

        var enabled = Providers.Where(p => _settings.IsProviderEnabled(p.Id)).ToList();

        // Fetch every enabled provider in parallel; marshal results back to the UI thread.
        var tasks = enabled.Select(async card =>
        {
            var provider = _registry.Get(card.Id);
            if (provider is null) return;

            ProviderUsage usage;
            try
            {
                usage = await provider.FetchAsync();
            }
            catch (Exception ex)
            {
                usage = new ProviderUsage
                {
                    ProviderId = provider.Id,
                    DisplayName = provider.DisplayName,
                    State = ProviderState.Error,
                    ErrorMessage = ex.Message
                };
            }

            // Persist any error to the log file (both thrown and Error-state results).
            if (usage.State == ProviderState.Error)
            {
                ErrorLog.Write(provider.Id, provider.DisplayName, usage.ErrorMessage);
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() => card.Apply(usage));
        });

        await Task.WhenAll(tasks);

        LastUpdated = DateTime.Now;
        IsLoading = false;
        StatusMessage = $"Updated {DateTime.Now:t}";
        UpdateFreshnessText();
    }

    [RelayCommand]
    public void CycleTint()
    {
        TintIndex = (TintIndex + 1) % WidgetTint.All.Count;
        _settings.TintIndex = TintIndex;
        _settingsService.Save(_settings);

        OnPropertyChanged(nameof(CurrentTint));
        OnPropertyChanged(nameof(TintPrimaryBrush));
        OnPropertyChanged(nameof(TintSecondaryBrush));
        OnPropertyChanged(nameof(TintGlowBrush));
    }

    [RelayCommand]
    public void ToggleCompact()
    {
        CompactMode = !CompactMode;
        _settings.CompactMode = CompactMode;
        _settingsService.Save(_settings);
    }

    /// <summary>
    /// Advances the Google card to the next account in the carousel (see GoogleProvider).
    /// Bound to the "›" button; no-op when fewer than 2 accounts exist.
    /// </summary>
    [RelayCommand]
    public void CycleGoogleAccount() => GoogleProvider.CycleNextAccount();

    /// <summary>
    /// Steps the Google card back to the previous account in the carousel. Bound to the
    /// "‹" button; no-op when fewer than 2 accounts exist.
    /// </summary>
    [RelayCommand]
    public void CycleGoogleAccountBack() => GoogleProvider.CyclePrevAccount();

    /// <summary>
    /// Runs the interactive Google OAuth flow and, on success, persists the new account.
    /// Called from Settings → Add Google account. Returns the added email on success,
    /// null on failure/cancellation (the error is surfaced to the caller to display).
    /// </summary>
    public async Task<(string Email, string? Error)> AddGoogleAccountAsync()
    {
        try
        {
            var (email, refreshToken) = await GoogleOAuthFlow.RunAsync();
            // De-dupe by email: if the account already exists, replace its token.
            _settings.GoogleAccounts.RemoveAll(a =>
                string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
            _settings.GoogleAccounts.Add(new GoogleAccount { Email = email, RefreshToken = refreshToken });
            _settingsService.Save(_settings);
            SyncGoogleAccountsToProvider();
            return (email, null);
        }
        catch (Exception ex)
        {
            return ("", ex.Message);
        }
    }

    /// <summary>Removes a configured Google account by email and persists settings.</summary>
    public void RemoveGoogleAccount(string email)
    {
        _settings.GoogleAccounts.RemoveAll(a =>
            string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
        _settingsService.Save(_settings);
        SyncGoogleAccountsToProvider();
    }

    /// <summary>The configured Google accounts (read-only view for the Settings UI).</summary>
    public IReadOnlyList<GoogleAccount> GetGoogleAccounts() => _settings.GoogleAccounts;

    /// <summary>
    /// Copies settings → the Google provider's configured-account list. The provider merges
    /// these with the auto-detected Antigravity account to form the carousel roster.
    /// </summary>
    private void SyncGoogleAccountsToProvider()
    {
        GoogleProvider.ConfiguredAccounts = _settings.GoogleAccounts.ToList();
    }

    public void SaveSettings()
    {
        _settings.TintIndex = TintIndex;
        _settings.AutoRefreshEnabled = AutoRefreshEnabled;
        _settings.RefreshIntervalSeconds = RefreshIntervalSeconds;
        _settings.MeterStyleName = MeterStyle.ToString();
        _settings.AlwaysOnTop = AlwaysOnTop;
        _settings.CompactMode = CompactMode;
        _settingsService.Save(_settings);
    }

    // --- Private methods ---

    private void ApplySettings(SettingsData settings)
    {
        TintIndex = settings.TintIndex;
        AutoRefreshEnabled = settings.AutoRefreshEnabled;
        RefreshIntervalSeconds = settings.RefreshIntervalSeconds;
        AlwaysOnTop = settings.AlwaysOnTop;
        CompactMode = settings.CompactMode;
        if (Enum.TryParse<MeterStyle>(settings.MeterStyleName, out var style))
            MeterStyle = style;
    }

    private void UpdateFreshnessText()
    {
        FreshnessText = LastUpdated.HasValue
            ? $"All providers refreshed {LastUpdated.Value:t}"
            : "Not refreshed yet";
    }
}
