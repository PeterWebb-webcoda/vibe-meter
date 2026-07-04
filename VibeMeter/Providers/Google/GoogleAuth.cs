using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Detects the local Google Antigravity (Gemini) setup and supplies an OAuth2 access token
/// for the Cloud Code usage API.
/// </summary>
/// <remarks>
/// <para><b>Auth source:</b></para>
/// <para>
/// Antigravity stores its OAuth refresh token in its IDE state database at
/// <c>%APPDATA%\Antigravity\User\globalStorage\state.vscdb</c>, under the
/// <c>ItemTable</c> key <c>antigravityUnifiedStateSync.oauthToken</c>. The value is a
/// base64-encoded protobuf message whose payload contains (in order) the access token,
/// the string <c>Bearer</c>, and the refresh token (<c>1//…</c>). We extract the refresh
/// token from the decoded protobuf bytes via regex — no protobuf library required.
/// </para>
/// <para>
/// <b>Why not <c>~/.gemini/oauth_creds.json</c>?</b> The Gemini CLI's refresh token lacks
/// the <c>https://www.googleapis.com/auth/cclog</c> scope that
/// <c>cloudcode-pa.googleapis.com</c> requires; calls fail with "The caller does not have
/// permission". Antigravity's token carries <c>cclog</c> and works. Refresh-token scopes
/// are fixed at authorisation time, so the CLI token cannot be elevated — we must use
/// Antigravity's.
/// </para>
/// <para>
/// The refresh token is exchanged for a fresh access token at
/// <c>https://oauth2.googleapis.com/token</c> using the public OAuth client credentials
/// shipped in the Antigravity Cockpit VS Code extension (<c>jlcodes.antigravity-cockpit</c>)
/// — the same client that minted Antigravity's token. Those client ID/secret values are not
/// secrets; they are distributed in the extension's bundled JavaScript.
/// </para>
/// <para>
/// Account identity (non-secret) comes from
/// <c>%USERPROFILE%\.gemini\google_accounts.json</c>.
/// </para>
/// </remarks>
public sealed class GoogleAuth
{
    private static readonly string HomePath =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string AppDataPath =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    /// <summary>Antigravity / Gemini CLI data directory.</summary>
    public static string GeminiDir => Path.Combine(HomePath, ".gemini");

    /// <summary>Records the active Google account email (non-secret identity only).</summary>
    public static string AccountsFilePath => Path.Combine(GeminiDir, "google_accounts.json");

    /// <summary>Antigravity IDE global state SQLite database (holds the OAuth token).</summary>
    public static string StateDbPath =>
        Path.Combine(AppDataPath, "Antigravity", "User", "globalStorage", "state.vscdb");

    /// <summary>The ItemTable key holding the base64-protobuf OAuth token blob.</summary>
    private const string TokenDbKey = "antigravityUnifiedStateSync.oauthToken";

    /// <summary>
    /// Public OAuth client credentials from the Antigravity Cockpit extension
    /// (<c>jlcodes.antigravity-cockpit</c> v2.1.52). Antigravity's refresh token was minted
    /// by this client, so it must be refreshed with the matching client — Google rejects
    /// refreshes from any other OAuth client. These values are not secrets; they are
    /// distributed in the extension's bundled JavaScript.
    /// </summary>
    private const string ClientId =
        "1071006060591-tmhssin2h21lcre235vtolojh4g403ep.apps.googleusercontent.com";
    private const string ClientSecret = "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";

    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";

    // Refresh 5 minutes before the recorded expiry so we never hand out a stale token.
    private const int RefreshSkewSeconds = 300;

    private static readonly HttpClient HttpClient = new();

    // Matches a Google refresh token (always starts with "1//") inside the decoded
    // protobuf bytes. Google refresh tokens are URL-safe: [A-Za-z0-9_-].
    private static readonly Regex RefreshTokenRegex =
        new(@"1//[A-Za-z0-9_-]{30,}", RegexOptions.Compiled);

    // Cached token + its absolute expiry (UTC).
    private GoogleTokenInfo? _cachedToken;

    /// <summary>True when Antigravity appears to be installed with a usable refresh token.</summary>
    public bool IsConfigured => GetRefreshToken() is not null ||
                                Directory.Exists(GeminiDir) ||
                                File.Exists(StateDbPath);

    /// <summary>A short, non-sensitive description of where the refresh token was found.</summary>
    public string DetectionLabel =>
        GetRefreshToken() is not null
            ? "Antigravity"
            : File.Exists(StateDbPath) ? "Antigravity (no token)" : "not found";

    /// <summary>
    /// Reads the OAuth refresh token from Antigravity's state database. Returns null when
    /// Antigravity is absent, not signed in, or the token blob is malformed.
    /// </summary>
    public string? GetRefreshToken()
    {
        if (!File.Exists(StateDbPath)) return null;

        string? base64Blob = null;
        try
        {
            // SqliteConnection needs a URI-style path with read-only mode to avoid locking
            // the live Antigravity process's database.
            var connString = new SqliteConnectionStringBuilder
            {
                DataSource = StateDbPath,
                Mode = SqliteOpenMode.ReadOnly,
            }.ToString();

            using var conn = new SqliteConnection(connString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = @key";
            cmd.Parameters.AddWithValue("@key", TokenDbKey);
            base64Blob = cmd.ExecuteScalar() as string;
        }
        catch
        {
            // DB locked, unreadable, or schema changed — treat as "no token".
            return null;
        }

        if (string.IsNullOrWhiteSpace(base64Blob)) return null;

        try
        {
            // The value is base64-encoded protobuf. Decode, then pull the refresh token
            // (a "1//..." ASCII string) straight out of the bytes — no protobuf parser needed.
            var bytes = Convert.FromBase64String(base64Blob);
            var text = System.Text.Encoding.ASCII.GetString(bytes);
            var match = RefreshTokenRegex.Match(text);
            return match.Success ? match.Value : null;
        }
        catch
        {
            return null;
        }
    }

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

    /// <summary>
    /// Returns a live OAuth2 access token, refreshing when the cached one is missing or
    /// near expiry. Throws on auth failure (the provider converts this to an Error state).
    /// </summary>
    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        if (_cachedToken is { } cached && cached.ExpiresAtUtc > now.AddSeconds(RefreshSkewSeconds))
        {
            return cached.AccessToken;
        }

        var refresh = GetRefreshToken()
                      ?? throw new InvalidOperationException(
                          "No Google Antigravity refresh token found at " +
                          $"{StateDbPath}. Sign in via the Antigravity IDE to enable Google usage.");

        var token = await ExchangeRefreshTokenAsync(refresh, ct);
        _cachedToken = token;
        return token.AccessToken;
    }

    private async Task<GoogleTokenInfo> ExchangeRefreshTokenAsync(
        string refreshToken, CancellationToken ct)
    {
        var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["client_secret"] = ClientSecret,
            ["refresh_token"] = refreshToken,
            ["grant_type"] = "refresh_token",
        });

        using var resp = await HttpClient.PostAsync(TokenEndpoint, body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            string detail = ParseErrorDetail(json) ?? $"{(int)resp.StatusCode} {resp.ReasonPhrase}";
            throw new InvalidOperationException($"Google token refresh failed: {detail}");
        }

        var exchanged = JsonSerializer.Deserialize<GoogleTokenResponse>(json)
                        ?? throw new InvalidOperationException(
                            "Google token refresh returned an empty response.");
        if (string.IsNullOrWhiteSpace(exchanged.AccessToken))
        {
            throw new InvalidOperationException(
                "Google token refresh returned no access token" +
                (exchanged.ErrorDescription is { } d ? $": {d}" : "."));
        }

        return new GoogleTokenInfo(
            AccessToken: exchanged.AccessToken!,
            ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(60, exchanged.ExpiresIn ?? 3600)));
    }

    private static string? ParseErrorDetail(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error_description", out var d) &&
                d.ValueKind == JsonValueKind.String)
            {
                return d.GetString();
            }
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString();
                if (e.ValueKind == JsonValueKind.Object &&
                    e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    return m.GetString();
                }
            }
        }
        catch
        {
            // ignore — fall back to status code
        }
        return null;
    }

    private sealed record GoogleTokenInfo(string AccessToken, DateTimeOffset ExpiresAtUtc);

    private sealed class GoogleTokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}
