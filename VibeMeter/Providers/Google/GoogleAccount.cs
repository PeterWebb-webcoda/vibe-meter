using System.Text.Json.Serialization;

namespace VibeMeter.Providers.Google;

/// <summary>
/// One Google account in the card's carousel: the email (for display / identification) and
/// the long-lived OAuth refresh token (used to mint short-lived access tokens for the Cloud
/// Code usage API). Accounts added via Settings → "Add Google account" are persisted in
/// <c>%APPDATA%\VibeMeter\settings.json</c>; the account Antigravity itself is signed into
/// is discovered at runtime and marked <see cref="IsAutoDetected"/>.
/// </summary>
public sealed class GoogleAccount
{
    /// <summary>The Google account email, e.g. <c>someone@example.com</c>.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// The OAuth2 refresh token (<c>1//...</c>). Sensitive at rest — stored in plaintext
    /// in <c>settings.json</c>, matching the Gemini CLI's <c>oauth_creds.json</c>
    /// convention. DPAPI encryption is a deferred follow-up.
    /// </summary>
    public string RefreshToken { get; set; } = "";

    /// <summary>
    /// True for the account discovered from the signed-in Antigravity IDE rather than added
    /// through VibeMeter's own OAuth flow. Not persisted — it is re-derived on every run,
    /// because the IDE's signed-in account can change independently of our settings.
    /// </summary>
    [JsonIgnore]
    public bool IsAutoDetected { get; set; }
}
