using System;
using System.IO;
using System.Text.Json;
using VibeMeter.Models;

namespace VibeMeter.Services;

/// <summary>
/// Loads and saves <see cref="SettingsData"/> to
/// <c>%APPDATA%\VibeMeter\settings.json</c>.
/// </summary>
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

    /// <summary>Returns defaults when the file is missing or unreadable.</summary>
    public SettingsData Load()
    {
        try
        {
            if (!File.Exists(SettingsFilePath))
            {
                return new SettingsData();
            }

            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<SettingsData>(json, JsonOptions) ?? new SettingsData();
        }
        catch
        {
            return new SettingsData();
        }
    }

    public void Save(SettingsData data)
    {
        Directory.CreateDirectory(SettingsDirectory);
        var json = JsonSerializer.Serialize(data, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }
}
