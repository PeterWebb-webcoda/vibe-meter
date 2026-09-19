using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using VibeMeter.Core.Security;
using VibeMeter.Providers.Google;
using VibeMeter.Ui.Models;
using VibeMeter.Ui.Services;
using VibeMeter.Ui.ViewModels;
using Xunit;

namespace VibeMeter.Tests.Ui;

/// <summary>
/// Covers the three defects that only appear where no secret store exists — which is
/// every non-Windows host, since DPAPI is the only <see cref="ISecretProtector"/>.
/// Each uses <see cref="UnavailableProtector"/> so the behaviour is asserted on any
/// platform, including Windows CI, rather than only where it happens to reproduce.
/// </summary>
public sealed class LinuxSettingsAndProtectionTests : IDisposable
{
    private readonly string _dir;

    public LinuxSettingsAndProtectionTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vibemeter-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private SettingsService Service(ISecretProtector protector) => new(protector, _dir);

    /// <summary>A carried-over settings file holding a pre-protection plaintext token.</summary>
    private string WriteLegacyFile(string email, string token)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, $$"""
        {
          "GoogleAccounts": [
            { "Email": "{{email}}", "RefreshToken": "{{token}}" }
          ]
        }
        """);
        return path;
    }

    // --- Section 4: opening a carried-over file must not destroy the token ---

    [Fact]
    public void Loading_a_legacy_file_without_a_protector_leaves_the_stored_token_untouched()
    {
        var path = WriteLegacyFile("someone@example.com", "legacy-refresh-token");
        var before = File.ReadAllText(path);

        var loaded = Service(new UnavailableProtector()).Load();

        Assert.Equal(before, File.ReadAllText(path));
        Assert.Contains("legacy-refresh-token", File.ReadAllText(path));
        Assert.Single(loaded.GoogleAccounts);
    }

    [Fact]
    public void A_legacy_token_stays_usable_in_memory_when_it_cannot_be_protected()
    {
        WriteLegacyFile("someone@example.com", "legacy-refresh-token");

        var account = Service(new UnavailableProtector()).Load().GoogleAccounts.Single();

        // Usable this session, so the account keeps working, but not re-persisted:
        // RefreshToken is [JsonIgnore], so nothing new reaches the disk.
        Assert.Equal("legacy-refresh-token", account.RefreshToken);
        Assert.False(account.NeedsReauthentication);
    }

    [Fact]
    public void A_legacy_token_is_migrated_and_removed_once_a_protector_is_available()
    {
        var path = WriteLegacyFile("someone@example.com", "legacy-refresh-token");

        var loaded = Service(new ReversingProtector()).Load();

        var written = File.ReadAllText(path);
        Assert.DoesNotContain("legacy-refresh-token", written);
        Assert.Equal("legacy-refresh-token", loaded.GoogleAccounts.Single().RefreshToken);
    }

    // --- Section 5: the add flow must not delete what is already configured ---

    [Fact]
    public void Adding_an_account_is_refused_before_the_browser_opens_when_nothing_can_protect_it()
    {
        var service = Service(new UnavailableProtector());
        var vm = new MainViewModel(new ProviderRegistry(new SettingsGoogleAccountSource(service)), service);

        Assert.False(vm.SecretProtectionAvailable);

        // Returns without ever reaching GoogleOAuthFlow — no consent screen, and so
        // no chance to discard a real token after the fact.
        var (email, error) = vm.AddGoogleAccountAsync().GetAwaiter().GetResult();

        Assert.Equal("", email);
        Assert.NotNull(error);
        Assert.DoesNotContain("Windows could not protect", error);
    }

    [Fact]
    public void A_refused_add_leaves_an_existing_account_in_the_file()
    {
        var service = Service(new ReversingProtector());
        var existing = new GoogleAccount { Email = "keep@example.com" };
        Assert.True(GoogleAccountProtection.Seal(existing, "keep-token", service.Protector));
        var data = new SettingsData();
        data.GoogleAccounts.Add(existing);
        service.Save(data);

        // A host where protection is unavailable now opens the same file and an add is attempted.
        var blocked = Service(new UnavailableProtector());
        var vm = new MainViewModel(new ProviderRegistry(new SettingsGoogleAccountSource(blocked)), blocked);
        vm.AddGoogleAccountAsync().GetAwaiter().GetResult();

        Assert.Equal("keep@example.com", blocked.Load().GoogleAccounts.Single().Email);
    }

    // --- Section 6: a save from the main view model must not revert other settings ---

    [Fact]
    public void Cycling_the_tint_does_not_revert_provider_toggles_or_publish_settings()
    {
        var service = Service(new UnavailableProtector());
        var vm = new MainViewModel(new ProviderRegistry(new SettingsGoogleAccountSource(service)), service);

        // Written by the settings window AFTER the view model took its snapshot.
        var settings = service.Load();
        settings.ProviderEnabled["codex"] = false;
        settings.LaunchAtStartup = true;
        settings.PublishEnabled = true;
        settings.PublishApiBaseUrl = "https://example.invalid";
        service.Save(settings);

        vm.CycleTint();

        var after = service.Load();
        Assert.False(after.ProviderEnabled["codex"]);
        Assert.True(after.LaunchAtStartup);
        Assert.True(after.PublishEnabled);
        Assert.Equal("https://example.invalid", after.PublishApiBaseUrl);
    }

    [Fact]
    public void Cycling_the_tint_still_persists_the_fields_the_main_view_model_owns()
    {
        var service = Service(new UnavailableProtector());
        var vm = new MainViewModel(new ProviderRegistry(new SettingsGoogleAccountSource(service)), service);

        var before = vm.TintIndex;
        vm.CycleTint();

        Assert.NotEqual(before, service.Load().TintIndex);
        Assert.Equal(vm.TintIndex, service.Load().TintIndex);
    }

    // --- Section 7: "Launch at Login" must not write into the working directory ---

    [Fact]
    public void Enabling_launch_at_login_writes_no_startup_file_into_the_working_directory()
    {
        var service = Service(new UnavailableProtector());
        var main = new MainViewModel(new ProviderRegistry(new SettingsGoogleAccountSource(service)), service);
        var settings = new SettingsViewModel(main, service);

        var stray = Path.Combine(Directory.GetCurrentDirectory(), "VibeMeter.bat");
        var strayExistedBefore = File.Exists(stray);

        settings.LaunchAtStartup = true;
        settings.Save();

        if (OperatingSystem.IsWindows())
        {
            // Windows keeps its Startup-folder launcher; nothing lands in the CWD either way.
            Assert.Equal(strayExistedBefore, File.Exists(stray));
            return;
        }

        // SpecialFolder.Startup is "" on Unix, so Path.Combine used to yield a RELATIVE
        // "VibeMeter.bat" - written into whatever directory the app was launched from, and
        // File.Delete'd from there on untick, which could remove an unrelated file.
        Assert.False(File.Exists(stray));
        Assert.True(settings.LaunchAtStartup);
    }

    // --- Saved settings must not be world-readable on Unix ---

    [Fact]
    public void Saving_settings_restricts_the_file_and_directory_to_the_owner_on_unix()
    {
        var service = Service(new UnavailableProtector());
        service.Save(new SettingsData());

        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        // No secret lives here - a refresh token is only ever stored protected - but the
        // Google account email list and the publish tenant/client ids do, and 0644 in a
        // 0755 directory hands those to every account on the machine.
        var fileMode = File.GetUnixFileMode(service.SettingsFilePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, fileMode);

        var dirMode = new DirectoryInfo(_dir).UnixFileMode;
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            dirMode);
    }

    // --- Fakes ---

    private sealed class UnavailableProtector : ISecretProtector
    {
        public bool TryProtect(string plaintext, out string protectedValue)
        { protectedValue = ""; return false; }

        public bool TryUnprotect(string protectedValue, out string plaintext)
        { plaintext = ""; return false; }
    }

    /// <summary>Reversible stand-in for a working protector. Not encryption; a test double.</summary>
    private sealed class ReversingProtector : ISecretProtector
    {
        public bool TryProtect(string plaintext, out string protectedValue)
        {
            protectedValue = new string(plaintext.Reverse().ToArray());
            return !string.IsNullOrEmpty(plaintext);
        }

        public bool TryUnprotect(string protectedValue, out string plaintext)
        {
            plaintext = new string(protectedValue.Reverse().ToArray());
            return !string.IsNullOrEmpty(protectedValue);
        }
    }
}
