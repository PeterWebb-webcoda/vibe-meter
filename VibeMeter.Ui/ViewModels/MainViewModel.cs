using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeMeter.Core;
using VibeMeter.Ui.Models;
using VibeMeter.Providers.Google;
using VibeMeter.Ui.Services;

namespace VibeMeter.Ui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly ProviderRegistry _registry;
    private readonly SettingsService _settingsService;
    private SettingsData _settings;

    /// <summary>
    /// Where each refresh's reports are sent, or null when the user has not
    /// opted in to publishing (the default). Owned and replaced by the
    /// host app, which holds the tray icon the sign-in prompt needs.
    /// </summary>
    private VibeMeter.Publishing.DesktopPublishHost? _publishHost;

    /// <summary>
    /// The UI thread this view model was created on, so background fetches
    /// can marshal each card update back to it. The WPF app constructs this
    /// view model on its dispatcher thread, where the context is the
    /// dispatcher's own; a null context (a headless host) skips the marshal.
    /// </summary>
    private readonly SynchronizationContext? _uiContext = SynchronizationContext.Current;

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
    public Rgb TintPrimary => CurrentTint.Primary;
    public Rgb TintSecondary => CurrentTint.Secondary;
    public Rgb TintGlow => CurrentTint.Glow;

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

            _uiContext?.Send(_ => card.Apply(usage), null);
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
        PersistSettings();

        OnPropertyChanged(nameof(CurrentTint));
        OnPropertyChanged(nameof(TintPrimary));
        OnPropertyChanged(nameof(TintSecondary));
        OnPropertyChanged(nameof(TintGlow));
    }

    [RelayCommand]
    public void ToggleCompact()
    {
        CompactMode = !CompactMode;
        PersistSettings();
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
    /// Whether this machine can protect a secret well enough to store one, tested by
    /// asking the configured protector to protect a throwaway value.
    /// </summary>
    /// <remarks>
    /// Probing rather than checking the operating system keeps this true of whatever
    /// protector is configured, including a future non-Windows one — and a host can
    /// use it to disable "Add Google account" instead of offering a flow that cannot
    /// finish. The probe value is a constant, never a real credential.
    /// </remarks>
    public bool SecretProtectionAvailable =>
        _settingsService.Protector.TryProtect("vibemeter-protector-probe", out _);

    /// <summary>
    /// Runs the interactive Google OAuth flow and, on success, persists the new account.
    /// Called from Settings → Add Google account. Returns the added email on success,
    /// null on failure/cancellation (the error is surfaced to the caller to display).
    /// </summary>
    public async Task<(string Email, string? Error)> AddGoogleAccountAsync()
    {
        try
        {
            // Checked BEFORE the browser opens. Sealing is what fails when there is
            // no secret store, and it used to fail after a real Google consent had
            // already been completed — walking someone through an OAuth grant that
            // was always going to be discarded.
            if (!SecretProtectionAvailable)
            {
                return ("", "This machine has no secret store VibeMeter can use, so a Google " +
                            "refresh token cannot be saved safely and the account was not added. " +
                            "Secret protection is currently implemented for Windows only.");
            }

            var (email, refreshToken) = await GoogleOAuthFlow.RunAsync();

            var account = new GoogleAccount { Email = email };
            if (!GoogleAccountProtection.Seal(account, refreshToken, _settingsService.Protector))
            {
                // Storing it in the clear is not an option, so the account is not
                // stored at all. Nothing has been removed at this point: the de-dupe
                // below runs only once there is a replacement to put in place.
                return ("", "The Google refresh token could not be protected on this machine, " +
                            "so the account was not saved. Nothing already configured has changed.");
            }

            // De-dupe by email, now that the new account is sealed and can replace
            // the old one. Doing this before the seal meant a failure dropped the
            // existing account from the in-memory settings, and the next save of
            // those settings from anywhere else persisted the deletion.
            PersistSettings(s =>
            {
                s.GoogleAccounts.RemoveAll(a =>
                    string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase));
                s.GoogleAccounts.Add(account);
            });
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
        PersistSettings(s => s.GoogleAccounts.RemoveAll(a =>
            string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The configured Google accounts (read-only view for the Settings UI).</summary>
    public IReadOnlyList<GoogleAccount> GetGoogleAccounts() => _settings.GoogleAccounts;

    public void SaveSettings() => PersistSettings();

    /// <summary>
    /// Writes this view model's own fields, plus an optional extra mutation, without
    /// discarding anything another part of the app has written.
    /// </summary>
    /// <remarks>
    /// The file is re-read first. <c>_settings</c> is the snapshot taken when this view
    /// model was constructed, and the settings window owns fields that are not on it —
    /// the provider enable toggles, launch-at-login and all five publish values. Saving
    /// the snapshot wrote those back as they were at app start, so cycling the tint or
    /// toggling compact silently reverted them. That is the bug people report as
    /// "it forgot my settings".
    /// </remarks>
    private void PersistSettings(Action<SettingsData>? alsoApply = null)
    {
        var settings = _settingsService.Load();

        settings.TintIndex = TintIndex;
        settings.AutoRefreshEnabled = AutoRefreshEnabled;
        settings.RefreshIntervalSeconds = RefreshIntervalSeconds;
        settings.MeterStyleName = MeterStyle.ToString();
        settings.AlwaysOnTop = AlwaysOnTop;
        settings.CompactMode = CompactMode;

        alsoApply?.Invoke(settings);

        _settingsService.Save(settings);
        _settings = settings;
    }

    /// <summary>
    /// Points the refresh at a publish host, or at nothing. Called by the
    /// host app at startup and again whenever the publishing settings
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
