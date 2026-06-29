using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Detects the local Google AI Pro / Antigravity (Gemini) setup. Antigravity stores its
/// data under <c>%USERPROFILE%\.gemini\</c>; the signed-in Google account is recorded in
/// <c>google_accounts.json</c>. The OAuth tokens in <c>oauth_creds.json</c> are never
/// read — only the non-sensitive account identity.
/// </summary>
public sealed class GoogleAuth
{
    private static readonly string HomePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Antigravity / Gemini CLI data directory.</summary>
    public static string GeminiDir => Path.Combine(HomePath, ".gemini");

    /// <summary>Records the active Google account email (non-secret identity only).</summary>
    public static string AccountsFilePath => Path.Combine(GeminiDir, "google_accounts.json");

    /// <summary>Antigravity IDE state directory.</summary>
    public static string AntigravityDir => Path.Combine(GeminiDir, "antigravity-ide");

    /// <summary>True when Antigravity (or the Gemini CLI) appears to be installed.</summary>
    public bool IsConfigured => Directory.Exists(GeminiDir) &&
        (Directory.Exists(AntigravityDir) || File.Exists(AccountsFilePath));

    /// <summary>The signed-in Google account email, or null when not available.</summary>
    public async Task<string?> GetAccountEmailAsync()
    {
        if (!File.Exists(AccountsFilePath)) return null;

        try
        {
            await using var stream = File.OpenRead(AccountsFilePath);
            using var doc = await JsonDocument.ParseAsync(stream);
            return doc.RootElement.TryGetProperty("active", out var active) &&
                   active.ValueKind == JsonValueKind.String
                ? active.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }
}
