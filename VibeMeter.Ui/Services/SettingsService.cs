using System;
using System.IO;
using System.Text.Json;
using VibeMeter.Core.Security;
using VibeMeter.Ui.Models;
using VibeMeter.Providers.Google;

namespace VibeMeter.Ui.Services;

/// <summary>
/// Loads and saves <see cref="SettingsData"/> to
/// <c>%APPDATA%\VibeMeter\settings.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Google refresh tokens.</b> Builds up to 0.5.1 wrote each account's OAuth
/// refresh token into this file in the clear. A Google refresh token is
/// long-lived and does not expire on its own, and this is the file people paste
/// into bug reports, so <see cref="Load"/> migrates any it finds: the token is
/// re-stored in its protected form and the file is written back immediately,
/// which is what removes the plaintext from disk. See
/// <see cref="GoogleAccountProtection"/>.
/// </para>
/// </remarks>
public class SettingsService
{
    private static readonly string DefaultSettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeMeter");

    /// <summary>
    /// Where this instance reads and writes. Per-instance rather than static so a test
    /// can point one at a temporary directory: the view models were moved into this
    /// project precisely so they could be exercised, and they cannot be while every
    /// instance shares one path under the real profile.
    /// </summary>
    private readonly string _settingsDirectory;
    private readonly string _settingsFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// How stored secrets in this file are protected. Exposed so the account-add
    /// flow seals a new token with exactly what <see cref="Load"/> will open it
    /// with, rather than its own guess.
    /// </summary>
    public ISecretProtector Protector { get; }

    public SettingsService() : this(DpapiSecretProtector.Instance) { }

    /// <summary>Testable constructor.</summary>
    public SettingsService(ISecretProtector protector, string? settingsDirectory = null)
    {
        Protector = protector;
        _settingsDirectory = settingsDirectory ?? DefaultSettingsDirectory;
        _settingsFilePath = Path.Combine(_settingsDirectory, "settings.json");
    }

    /// <summary>The file this instance persists to, exposed so a test can inspect it.</summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>Returns defaults when the file is missing or unreadable.</summary>
    /// <remarks>
    /// Callers that are about to WRITE what they read must use <see cref="TryLoad"/>
    /// instead. This overload cannot tell "there is nothing yet" from "it could not be
    /// read", and overlaying a few fields onto defaults and saving that is how a whole
    /// settings file gets replaced by one transient read failure.
    /// </remarks>
    public SettingsData Load() => TryLoad(out var data) ? data : new SettingsData();

    /// <summary>
    /// Reads the settings, reporting whether the read itself succeeded.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the file was read, or is simply not there yet — in both
    /// cases <paramref name="data"/> is safe to build on. <see langword="false"/> when the
    /// file exists but could not be read or parsed, in which case <paramref name="data"/> is
    /// defaults that must NOT be written back over it.
    /// </returns>
    /// <remarks>
    /// The distinction is the whole point. <see cref="Save"/> writes the file whole, and
    /// <see cref="SettingsGoogleAccountSource"/> loads it from provider fetch threads, so a
    /// sharing violation between the two is ordinary rather than exotic. Treating that
    /// failure as "no settings yet" would discard every Google account and its protected
    /// token, every provider toggle and the whole publish block.
    /// </remarks>
    public bool TryLoad(out SettingsData data)
    {
        data = new SettingsData();

        SettingsData read;
        try
        {
            if (!File.Exists(_settingsFilePath))
            {
                return true;
            }

            var json = ReadSharing(_settingsFilePath);
            read = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions) ?? new SettingsData();
        }
        catch
        {
            // Deliberately not narrowed to IOException. A parse failure on a file that is
            // present is equally a reason not to overwrite it: with an atomic Save a reader
            // never sees a half-written file, so malformed content means something else
            // wrote it, and that is not ours to discard.
            return false;
        }

        data = read;

        try
        {
            // Opens each stored token for this session, and migrates any that a previous
            // build left in the clear. The write-back is the migration — but only where the
            // plaintext could be replaced with a protected form; where it could not, Unseal
            // reports no change and this file is deliberately left exactly as it was.
            if (GoogleAccountProtection.Unseal(data.GoogleAccounts, Protector))
            {
                Save(data);
            }
            else
            {
                // A file that is not being rewritten still needs its mode narrowed: on a host
                // with no protector that file is precisely the one that may hold a plaintext
                // refresh token, and Save is the only other place this happens.
                RestrictToOwner();
            }
        }
        catch
        {
            // A settings read must not fail because the file could not be
            // rewritten; the next load tries again. Nothing is logged, because
            // the only thing worth saying here would be about a credential.
        }

        return true;
    }

    /// <summary>
    /// Reads the file while still allowing <see cref="Save"/> to replace it underneath us.
    /// </summary>
    /// <remarks>
    /// <see cref="File.ReadAllText(string)"/> opens with <c>FileShare.Read</c>, which on
    /// Windows denies the rename in Save: MoveFileEx refuses to replace a destination that
    /// anyone holds open, and the caller sees UnauthorizedAccessException. POSIX rename(2)
    /// has no such rule, so the atomic save worked on Linux and threw on Windows in exactly
    /// the concurrent case it was written for. Sharing Delete as well as ReadWrite gives
    /// Windows the same semantics Linux already had.
    /// </remarks>
    private static string ReadSharing(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Writes the settings file whole, atomically.
    /// </summary>
    /// <remarks>
    /// Via a temporary file in the same directory and then a rename, so a reader either sees
    /// the previous file or the new one and never a partial write. A plain WriteAllText left
    /// a window in which a concurrent load — which happens on every provider fetch — could
    /// read a truncated file, and the caller that then saved what it had read would make the
    /// damage permanent.
    /// </remarks>
    public void Save(SettingsData data)
    {
        Directory.CreateDirectory(_settingsDirectory);
        var json = JsonSerializer.Serialize(data, JsonOptions);

        var temp = _settingsFilePath + ".tmp";
        File.WriteAllText(temp, json);
        RestrictToOwner(temp);

        // File.Replace, not File.Move(overwrite). On Unix either is rename(2) and both
        // work, but on Windows File.Move is MoveFileEx, which refuses to replace a
        // destination anyone holds open — the exact case this method exists to make safe,
        // so the atomic save threw on Windows precisely when it mattered. ReplaceFile is
        // the API for that, and it needs the reader to share Delete as well: measured, both
        // halves are required and neither works alone. See ReadSharing.
        if (File.Exists(_settingsFilePath))
        {
            File.Replace(temp, _settingsFilePath, null, ignoreMetadataErrors: true);
        }
        else
        {
            // Nothing to replace on the first save, and Replace demands a destination.
            File.Move(temp, _settingsFilePath);
        }

        RestrictToOwner();
    }

    /// <summary>
    /// Narrows the settings file and its directory to the owning user on Unix.
    /// </summary>
    /// <remarks>
    /// The defaults are 0644 in a 0755 directory, which is world-readable. No secret is
    /// stored here — a Google refresh token is only ever written in protected form — but
    /// the file does hold the configured Google account email list and, once publishing is
    /// enabled, the tenant and client ids. That is not something to leave readable by every
    /// account on a shared machine. Windows is left alone: it inherits the profile's ACL.
    /// Failure is ignored deliberately; a settings write must not fail over a mode change.
    /// </remarks>
    private void RestrictToOwner(string? path = null)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        try
        {
            File.SetUnixFileMode(path ?? _settingsFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
            new DirectoryInfo(_settingsDirectory).UnixFileMode =
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        }
        catch
        {
            // Best effort: an unwritable mode is not a reason to lose the settings.
        }
    }
}
