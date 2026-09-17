using System;
using System.IO;
using System.Text;
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

    /// <summary>
    /// Reads the access token's own <c>exp</c> claim. The Codex CLI issues a
    /// short-lived token (observed: ten days), and it is only refreshed while the
    /// CLI actually runs on this machine. On an unattended box - a headless agent
    /// on a server, or a workstation left running while its owner is away - the
    /// token can therefore lapse with nothing to renew it. Surfacing the expiry
    /// lets the caller say so plainly instead of reporting an opaque 401.
    /// </summary>
    /// <returns>The expiry instant, or null when it cannot be determined.</returns>
    public static DateTimeOffset? ReadExpiry(string? accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var parts = accessToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            // Decode the payload only. This is a claims READ, never a validation:
            // the token is not ours to verify and we make no trust decision on it.
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            return document.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var seconds)
                ? DateTimeOffset.FromUnixTimeSeconds(seconds)
                : null;
        }
        catch
        {
            // A malformed or unexpected token is not an error worth failing on;
            // the caller simply loses the expiry hint.
            return null;
        }
    }

    /// <summary>The resolved auth file path, for diagnostics.</summary>
    public static string AuthFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
}
