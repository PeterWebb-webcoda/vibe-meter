using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Codex;

/// <summary>
/// Reads the Codex access token from the local auth file written by the Codex CLI
/// (<c>%USERPROFILE%\.codex\auth.json</c>).
/// </summary>
public sealed class CodexAuth
{
    private sealed class CodexAuthFile
    {
        [JsonPropertyName("tokens")]
        public CodexTokens? Tokens { get; set; }
    }

    private sealed class CodexTokens
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }

    /// <summary>Returns the access token, or null when not signed in on this PC.</summary>
    public async Task<string?> GetAccessTokenAsync()
    {
        string homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string authFilePath = Path.Combine(homePath, ".codex", "auth.json");

        if (!File.Exists(authFilePath))
        {
            return null;
        }

        await using var stream = File.OpenRead(authFilePath);
        var authFile = await JsonSerializer.DeserializeAsync<CodexAuthFile>(stream);

        return authFile?.Tokens?.AccessToken?.Trim();
    }

    /// <summary>The resolved auth file path, for diagnostics.</summary>
    public static string AuthFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
}
