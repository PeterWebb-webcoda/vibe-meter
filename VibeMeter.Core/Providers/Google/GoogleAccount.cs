using System.Text.Json.Serialization;

namespace VibeMeter.Providers.Google;

/// <summary>
/// One Google account in the card's carousel: the email (for display / identification) and
/// the long-lived OAuth refresh token (used to mint short-lived access tokens for the Cloud
/// Code usage API). Accounts added via Settings → "Add Google account" are persisted in
/// <c>%APPDATA%\VibeMeter\settings.json</c>; the account Antigravity itself is signed into
/// is discovered at runtime and marked <see cref="IsAutoDetected"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>What of this reaches the settings file.</b> Only <see cref="Email"/> and
/// <see cref="ProtectedRefreshToken"/>. The usable token lives in
/// <see cref="RefreshToken"/>, which is <see cref="JsonIgnoreAttribute">ignored</see>
/// and therefore exists in memory only — a Google refresh token is long-lived
/// and does not expire on its own, and settings.json is exactly the file
/// someone pastes into a bug report.
/// <see cref="VibeMeter.Core.Security.ISecretProtector"/> moves the value
/// between the two forms; <see cref="GoogleAccountProtection"/> drives it.
/// </para>
/// <para>
/// <see cref="LegacyRefreshToken"/> is the migration seam: it is the
/// <c>"RefreshToken"</c> property that pre-protection files hold in the clear.
/// It is read on load and, <em>where this host can protect the value</em>, written
/// back as <see langword="null"/> — which omits the property altogether, so the
/// plaintext leaves the file. Where it cannot be protected the legacy value is left
/// exactly as found: destroying a credential the user still holds would be worse
/// than leaving plaintext that was already in the file. See
/// <see cref="GoogleAccountProtection.Unseal"/>.
/// </para>
/// </remarks>
public sealed class GoogleAccount
{
    /// <summary>The Google account email, e.g. <c>someone@example.com</c>.</summary>
    public string Email { get; set; } = "";

    /// <summary>
    /// The refresh token as stored: protected by
    /// <see cref="VibeMeter.Core.Security.DpapiSecretProtector"/> and base64-encoded,
    /// openable only by this Windows user on this machine. Null when the account
    /// has no usable stored credential (see <see cref="NeedsReauthentication"/>).
    /// </summary>
    /// <remarks>
    /// Still user-and-machine-bound secret material, even if it is useless to
    /// anyone else: it is not something to paste into a bug report either. The
    /// name says "protected" rather than "token" so that is obvious at a glance.
    /// </remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ProtectedRefreshToken { get; set; }

    /// <summary>
    /// The plaintext refresh token as written by builds before protection existed.
    /// Read on load by <see cref="GoogleAccountProtection.Unseal"/>, which clears it —
    /// so it disappears from the file on the first load after upgrading — but ONLY on a
    /// host that could protect the value. Where none can, it is left in place and does
    /// get written back, which is the deliberate lesser harm.
    /// </summary>
    [JsonPropertyName("RefreshToken")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyRefreshToken { get; set; }

    /// <summary>
    /// The usable OAuth2 refresh token (<c>1//...</c>). In memory only — never
    /// serialised — and empty when the stored form could not be opened here.
    /// </summary>
    [JsonIgnore]
    public string RefreshToken { get; set; } = "";

    /// <summary>
    /// True for the account discovered from the signed-in Antigravity IDE rather than added
    /// through VibeMeter's own OAuth flow. Not persisted — it is re-derived on every run,
    /// because the IDE's signed-in account can change independently of our settings.
    /// </summary>
    [JsonIgnore]
    public bool IsAutoDetected { get; set; }

    /// <summary>
    /// True when this account has no usable credential in this session: the
    /// protected value could not be opened (a roamed profile, a restored
    /// backup, another user's blob), or protecting it failed so it was never
    /// stored. The account needs adding again; nothing here can recover it, and
    /// the deliberate alternative — keeping the plaintext — is the defect this
    /// design removes.
    /// </summary>
    [JsonIgnore]
    public bool NeedsReauthentication { get; set; }
}
