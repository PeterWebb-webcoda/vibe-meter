using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Codex;

/// <summary>
/// Renews the Codex access token from the refresh token the CLI already stored,
/// for machines where the CLI itself is not running often enough to do it.
/// </summary>
/// <remarks>
/// <para>
/// This is <b>opt-in</b> via <see cref="EnabledVariable"/> and defaults to off.
/// It rewrites a credential file that belongs to another application, so the
/// cost of a defect is the user's Codex sign-in, not merely this tool's data.
/// </para>
/// <para>
/// Two safeguards follow from that. The file is round-tripped through
/// <see cref="JsonNode"/> so fields this code does not understand survive
/// untouched, rather than being dropped by a narrow model. And the replacement
/// is written to a temporary file and moved into place, so an interrupted write
/// cannot leave a half-written credential behind.
/// </para>
/// </remarks>
public static class CodexTokenRefresh
{
    public const string EnabledVariable = "VIBEMETER_CODEX_AUTO_REFRESH";

    private const string TokenEndpoint = "https://auth.openai.com/oauth/token";

    /// <summary>Renew only when expiry is close, so a defect cannot burn the refresh token every cycle.</summary>
    private static readonly TimeSpan RenewWithin = TimeSpan.FromHours(12);

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable(EnabledVariable) is "1" or "true" or "TRUE";

    /// <summary>
    /// Refreshes the stored token when it is enabled, close to expiry, and the
    /// file holds what is needed. Returns the new access token, or null when no
    /// refresh was attempted or it did not succeed — callers keep using the
    /// existing token and report the eventual failure normally.
    /// </summary>
    public static async Task<string?> TryRefreshAsync(
        HttpClient httpClient,
        DateTimeOffset? currentExpiry,
        CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
        {
            return null;
        }

        if (currentExpiry is not null && currentExpiry - DateTimeOffset.UtcNow > RenewWithin)
        {
            return null;
        }

        var path = CodexAuth.AuthFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        JsonNode? document;
        try
        {
            document = JsonNode.Parse(await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception)
        {
            return null;
        }

        var tokens = document?["tokens"];
        var refreshToken = tokens?["refresh_token"]?.GetValue<string>();
        var clientId = CodexAuth.ReadClientId(tokens?["access_token"]?.GetValue<string>());

        if (document is null || tokens is null
            || string.IsNullOrWhiteSpace(refreshToken)
            || string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        JsonElement payload;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken!,
                    ["client_id"] = clientId!,
                }),
            };

            using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                // Deliberately does not include the body: it can echo the token.
                return null;
            }

            using var parsed = JsonDocument.Parse(
                await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            payload = parsed.RootElement.Clone();
        }
        catch (Exception)
        {
            return null;
        }

        if (!payload.TryGetProperty("access_token", out var accessTokenElement))
        {
            return null;
        }

        var accessToken = accessTokenElement.GetString();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        tokens["access_token"] = accessToken;

        // The endpoint may or may not rotate the refresh token. Persist a new one
        // when given; keep the existing one when not, rather than blanking it.
        if (payload.TryGetProperty("refresh_token", out var rotated)
            && rotated.GetString() is { Length: > 0 } rotatedValue)
        {
            tokens["refresh_token"] = rotatedValue;
        }

        if (payload.TryGetProperty("id_token", out var idToken)
            && idToken.GetString() is { Length: > 0 } idTokenValue)
        {
            tokens["id_token"] = idTokenValue;
        }

        document["last_refresh"] = DateTimeOffset.UtcNow.ToString("o");

        return TryWriteAtomically(path, document) ? accessToken : null;
    }

    /// <summary>
    /// Writes to a sibling temporary file and moves it into place, so a crash or
    /// a full disk cannot leave a truncated credential file behind.
    /// </summary>
    private static bool TryWriteAtomically(string path, JsonNode document)
    {
        var temporary = path + ".vibemeter.tmp";
        try
        {
            File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
            catch (Exception)
            {
                // Nothing further to do; the original file is untouched either way.
            }

            return false;
        }
    }
}
