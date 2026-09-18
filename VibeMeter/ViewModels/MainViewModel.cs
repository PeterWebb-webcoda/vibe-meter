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

    /// <summary>
    /// Where each refresh's reports are sent, or null when the user has not
    /// opted in to publishing (the default). Owned and replaced by
    /// <see cref="App"/>, which holds the tray icon the sign-in prompt needs.
    /// </summary>
    private VibeMeter.Publishing.DesktopPublishHost? _publishHost;

    /// <summary>
    /// Notices when a provider starts, stops or changes reading from a fallback source, so
    /// the error log gets one line per change rather than one per minute. See
    /// <see cref="ProviderUsage.SourceDiagnostic"/> for why that line has to exist at all.
    /// </summary>
    private readonly VibeMeter.Publishing.SourceDiagnosticTracker _sourceDiagnostics = new();

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

    public MainViewModel() : this(new SettingsService()) { }

    /// <summary>
    /// Builds the registry around the SAME settings service this view model
    /// uses, so the convenience constructor cannot hand the Google provider a
    /// registry that knows nothing about this host's accounts — which would
    /// publish "not configured" for an account sitting in settings.json.
    /// </summary>
    private MainViewModel(SettingsService settingsService)
        : this(new ProviderRegistry(new SettingsGoogleAccountSource(settingsService)), settingsService) { }

    public MainViewModel(ProviderRegistry registry, SettingsService settingsService)
    {
        _registry = registry;
        _settingsService = settingsService;
        _settings = _settingsService.Load();
        ApplySettings(_settings);

        // The Google provider reads its configured accounts from the settings
        // file itself, on every fetch (see SettingsGoogleAccountSource), so
        // there is deliberately nothing to seed here: a provider populated once
        // from this constructor is a provider that reports "not configured"
        // whenever this constructor was not the thing that built it.

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
        // Each task also RETURNS its report, so the refresh has the whole cycle's readings
        // in one place to hand to publishing. Task.WhenAll preserves the order of its input,
        // so that list is always in registry order — which is not cosmetic: the publish
        // policy compares a hash of the mapped document, provider order included, so a list
        // whose order varied with fetch timing would read as new figures every single cycle.
        var tasks = enabled.Select(async card =>
        {
            var provider = _registry.Get(card.Id);
            if (provider is null) return (ProviderUsage?)null;

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

            // A successful reading that came from a FALLBACK source is not an error — the
            // card is right — but a preferred source failing on every cycle is a fault, and
            // this is the only place the tray can say so. Once per change, never per cycle.
            if (_sourceDiagnostics.NoteChange(usage) is { } sourceNote)
            {
                ErrorLog.Write(provider.Id, provider.DisplayName, sourceNote);
            }

            System.Windows.Application.Current?.Dispatcher.Invoke(() => card.Apply(usage));
            return usage;
        });

        var collected = await Task.WhenAll(tasks);

        LastUpdated = DateTime.Now;
        IsLoading = false;
        StatusMessage = $"Updated {DateTime.Now:t}";
        UpdateFreshnessText();

        // Deliberately the last thing, and deliberately NOT awaited — see PublishCollected.
        PublishCollected(collected);
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

            var account = new GoogleAccount { Email = email };
            if (!GoogleAccountProtection.Seal(account, refreshToken, _settingsService.Protector))
            {
                // Storing it in the clear is not an option, so the account is
                // not stored at all and the person is told why.
                return ("", "Windows could not protect the Google refresh token for this profile, " +
                            "so the account was not saved. This usually means a roaming or " +
                            "temporary profile; try again on the machine's own account.");
            }

            _settings.GoogleAccounts.Add(account);
            _settingsService.Save(_settings);
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
    }

    /// <summary>The configured Google accounts (read-only view for the Settings UI).</summary>
    public IReadOnlyList<GoogleAccount> GetGoogleAccounts() => _settings.GoogleAccounts;

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

    /// <summary>
    /// Points the refresh at a publish host, or at nothing. Called by
    /// <see cref="App"/> at startup and again whenever the publishing settings
    /// are saved, so switching the feature on takes effect without a restart.
    /// </summary>
    public void UsePublishHost(VibeMeter.Publishing.DesktopPublishHost? host) => _publishHost = host;

    // --- Private methods ---

    /// <summary>
    /// Hands one refresh's readings to publishing and returns immediately.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is called from a continuation on the UI thread — nothing in
    /// <see cref="RefreshAsync"/> uses <c>ConfigureAwait(false)</c>, and the
    /// refresh timer's Tick is an async-void handler — so it must not be
    /// awaited and must not throw. <see cref="VibeMeter.Publishing.DesktopPublishHost.Publish"/>
    /// guarantees both: it schedules the work onto the thread pool and swallows
    /// everything that happens there. Awaiting it would freeze the window for
    /// up to three 30-second HTTP attempts plus backoff, put the offline
    /// queue's file writes on the dispatcher, and let a publish failure escape
    /// through the async-void handler as an unhandled exception.
    /// </para>
    /// <para>
    /// Providers the user has switched off are published EXPLICITLY as
    /// disabled rather than left out. The collection API keeps the latest
    /// reading per provider, so a provider that merely stops being mentioned
    /// keeps its last reading for ever — the phone would go on showing a
    /// switched-off provider's final percentage as though it were current. See
    /// <see cref="VibeMeter.Publishing.ProviderRoster"/>.
    /// </para>
    /// </remarks>
    private void PublishCollected(IReadOnlyList<ProviderUsage?> collected)
    {
        if (_publishHost is null) return;

        var reported = collected.Where(usage => usage is not null).Select(usage => usage!).ToList();
        _publishHost.Publish(VibeMeter.Publishing.ProviderRoster.IncludingDisabled(reported, _registry.Providers));
    }

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
