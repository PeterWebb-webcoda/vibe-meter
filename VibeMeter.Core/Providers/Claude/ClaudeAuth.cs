using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// Detects the local Claude installation, reads non-secret account / plan metadata from
/// <c>.claude.json</c> (the <c>oauthAccount</c> block), and reads the OAuth access token
/// the Claude Code CLI stored in <c>.credentials.json</c> so usage can be fetched live.
/// </summary>
/// <remarks>
/// <para>
/// Reading the credential is new, and it corrects a premise this class used to state
/// outright: that "Claude keeps its usage files fresh while it runs, so no authenticated
/// API call is required". It does not. <c>usage_cache.json</c> is not a file Claude Code
/// maintains — it is written only on an explicit <c>/usage</c> or a limit-approaching
/// warning. Measured on a Linux box, 2h10m of continuous heavy use rewrote the history,
/// the policy limits and the credential, and left the usage cache untouched for over a
/// day. The Windows box that made the file source look dependable has it fresh only
/// because a personal statusline script fetches usage and writes that file itself.
/// </para>
/// <para>
/// This mirrors <c>CodexAuth</c>, which has read the Codex CLI's stored token for the same
/// reason since that provider was written.
/// </para>
/// </remarks>
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
    /// The CLI's OAuth credential. Shares <c>CLAUDE_CONFIG_DIR</c> handling with the usage
    /// cache it sits beside, so relocating that tree moves both together.
    /// </summary>
    public static string CredentialsFilePath => ClaudeUsageSources.ConfigFile(".credentials.json");

    /// <summary>
    /// True when any Claude surface has left usable state on this PC. Deliberately broader
    /// than "the CLI signed in": a user who only runs the desktop app never gets a
    /// <c>.claude.json</c>, but their usage history is still perfectly readable — and a
    /// freshly signed-in machine has a credential before it has written any usage file.
    /// </summary>
    public bool IsConfigured =>
        File.Exists(SettingsFilePath)
        || File.Exists(CredentialsFilePath)
        || ClaudeUsageSources.AnyExists();

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

    /// <summary>
    /// Reads the OAuth access token and the plan fields stored alongside it, or null when
    /// the file is absent, unreadable, or holds no <c>claudeAiOauth</c> entry.
    /// </summary>
    /// <remarks>
    /// Never throws: a machine with no Claude Code sign-in is an ordinary, supported state,
    /// and it must cost nothing more than the loss of the live source.
    /// </remarks>
    public Task<ClaudeCredential?> GetCredentialAsync() => ReadCredentialAsync(CredentialsFilePath);

    /// <summary>
    /// Reads a named credential file rather than this machine's. Internal so tests can
    /// drive the real reader over a synthetic file.
    /// </summary>
    internal static async Task<ClaudeCredential?> ReadCredentialAsync(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<ClaudeCredentialsFile>(stream);

            // Absent claudeAiOauth entry: the file exists but belongs entirely to the MCP
            // servers that also keep credentials in it. Nothing here for us.
            if (file?.ClaudeAiOauth is not { } oauth) return null;

            return new ClaudeCredential
            {
                AccessToken = Trimmed(oauth.AccessToken),
                ExpiresAt = FromUnixMilliseconds(oauth.ExpiresAtUnixMilliseconds),
                SubscriptionType = Trimmed(oauth.SubscriptionType),
                RateLimitTier = Trimmed(oauth.RateLimitTier),
            };
        }
        catch
        {
            // A half-written, locked or malformed credential must not sink the provider —
            // the local files may still have perfectly good figures.
            return null;
        }
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// Converts the credential's <c>expiresAt</c> — Unix epoch <b>milliseconds</b> — to an
    /// instant.
    /// </summary>
    /// <remarks>
    /// The unit is the whole point of this method existing. Treating the value as seconds
    /// puts expiry somewhere around the year 56000, so a lapsed token would look valid
    /// forever and every poll would earn a 401; treating a seconds value as milliseconds
    /// puts it in 1970, so a perfectly good token would look lapsed and never be used. Out
    /// of range values yield null rather than throwing.
    /// </remarks>
    internal static DateTimeOffset? FromUnixMilliseconds(long? value)
    {
        if (value is not { } milliseconds) return null;

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
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

    /// <summary>
    /// Derives a plan label from the credential's stated <c>subscriptionType</c>, e.g.
    /// "max" -> "Claude Max".
    /// </summary>
    /// <remarks>
    /// This is the <b>last</b> resort, not the first. It is a stated value rather than an
    /// inferred one, but it is also a coarser one: the rate-limit tier already available
    /// from <c>.claude.json</c> distinguishes "Claude Max 5x" from "Claude Max 20x", and
    /// <c>subscriptionType</c> says only "max". Preferring the stated-but-vaguer field
    /// would lose information the card already shows today.
    /// </remarks>
    public static string? FriendlySubscription(string? subscriptionType)
    {
        var name = FriendlyTier(subscriptionType);
        if (name is null) return null;

        return name.StartsWith("Claude", StringComparison.OrdinalIgnoreCase) ? name : $"Claude {name}";
    }
}

/// <summary>
/// The Claude Code CLI's stored sign-in, narrowed to what this provider needs.
/// </summary>
/// <remarks>
/// <see cref="AccessToken"/> is a secret. It is passed to the usage endpoint and to
/// nothing else: it is never logged, never put in a message shown to the user, and never
/// written anywhere.
/// </remarks>
public sealed class ClaudeCredential
{
    public string? AccessToken { get; init; }

    /// <summary>When the access token lapses, converted from Unix milliseconds.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>The plan the subscription is on, as stated by the credential.</summary>
    public string? SubscriptionType { get; init; }

    /// <summary>The rate-limit tier, as stated by the credential.</summary>
    public string? RateLimitTier { get; init; }

    /// <summary>True when there is a token to call with.</summary>
    public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken);

    /// <summary>
    /// True when the token has lapsed. A credential with no stated expiry is treated as
    /// usable — the endpoint is the authority on that, and refusing to try would be worse
    /// than a 401 we can report.
    /// </summary>
    public bool IsExpiredAt(DateTimeOffset now) => ExpiresAt is { } expiry && expiry <= now;
}
