using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using VibeMeter.Core;
using VibeMeter.Models;
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

    [ObservableProperty] private int _refreshIntervalSeconds = 60;

    [ObservableProperty] private int _tintIndex;

    [ObservableProperty] private bool _alwaysOnTop = true;

    [ObservableProperty] private string _freshnessText = "";

    /// <summary>When true, the window renders as a slim horizontal strip instead of the full panel.</summary>
    [ObservableProperty] private bool _compactMode;

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
