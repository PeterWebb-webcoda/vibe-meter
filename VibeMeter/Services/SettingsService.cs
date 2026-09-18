using System;
using System.IO;
using System.Text.Json;
using VibeMeter.Core.Security;
using VibeMeter.Models;
using VibeMeter.Providers.Google;

namespace VibeMeter.Services;

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
    private static readonly string SettingsDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeMeter");

    private static readonly string SettingsFilePath =
        Path.Combine(SettingsDirectory, "settings.json");

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
    public SettingsService(ISecretProtector protector) => Protector = protector;

    /// <summary>Returns defaults when the file is missing or unreadable.</summary>
    public SettingsData Load()
    {
        SettingsData data;
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new SettingsData();
            }

            var json = File.ReadAllText(SettingsFilePath);
            data = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions) ?? new SettingsData();
        }
        catch
        {
            return new SettingsData();
        }

        try
        {
            // Opens each stored token for this session, and migrates any that a
            // previous build left in the clear. The write-back is the migration:
            // until it happens the plaintext is still in the file.
            if (GoogleAccountProtection.Unseal(data.GoogleAccounts, Protector))
            {
                Save(data);
            }
        }
        catch
        {
            // A settings read must not fail because the file could not be
            // rewritten; the next load tries again. Nothing is logged, because
            // the only thing worth saying here would be about a credential.
        }

        return data;
    }

    public void Save(SettingsData data)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }
}
