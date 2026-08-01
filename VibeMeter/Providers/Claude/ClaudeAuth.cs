using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// Detects the local Claude installation and reads non-secret account / plan metadata
/// from <c>%USERPROFILE%\.claude.json</c> (the <c>oauthAccount</c> block). The live
/// usage figures come from <see cref="ClaudeUsageSources"/>.
///
/// The secret token file (<c>%USERPROFILE%\.claude\.credentials.json</c>) is never
/// read: Claude keeps its usage files fresh while it runs, so no authenticated API
/// call is required.
/// </summary>
public sealed class ClaudeAuth
{
    private static readonly string HomePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Plan/identity metadata, written by the Claude Code CLI at sign-in.</summary>
    public static string SettingsFilePath
    {
        get
        {
            var configDir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return string.IsNullOrWhiteSpace(configDir)
                ? Path.Combine(HomePath, ".claude.json")
                : Path.Combine(configDir.Trim(), ".claude.json");
        }
    }

    /// <summary>
    /// True when any Claude surface has left usable state on this PC. Deliberately broader
    /// than "the CLI signed in": a user who only runs the desktop app never gets a
    /// <c>.claude.json</c>, but their usage history is still perfectly readable.
    /// </summary>
    public bool IsConfigured => File.Exists(SettingsFilePath) || ClaudeUsageSources.AnyExists();

    /// <summary>
    /// Reads the signed-in account / plan metadata, or null when not signed in or
    /// the file cannot be parsed.
    /// </summary>
    public async Task<ClaudeOAuthAccount?> GetAccountAsync()
    {
        if (!File.Exists(SettingsFilePath)) return null;

        try
        {
            await using var stream = File.OpenRead(SettingsFilePath);
            var settings = await JsonSerializer.DeserializeAsync<ClaudeSettingsFile>(stream);
            return settings?.OauthAccount;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Derives a friendly plan label from the rate-limit tier, e.g. "default_claude_max_5x" -> "Claude Max 5x".</summary>
    public static string? FriendlyTier(string? tier)
    {
        if (string.IsNullOrWhiteSpace(tier)) return null;

        var name = tier.Trim();
        const string prefix = "default_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            name = name[prefix.Length..];

        // Turn "claude_max_5x" into "Claude Max 5x", preserving alphanumeric tokens.
        var parts = name.Split('_', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var p = parts[i];
            parts[i] = char.ToUpperInvariant(p[0]) + p[1..];
        }
        return string.Join(' ', parts);
    }
}
