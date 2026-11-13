using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace VibeMeter.Providers.Claude;

/// <summary>
/// The account key for the account that owns the stored sign-in token, asked of Anthropic
/// once per token and remembered for the life of the process.
/// </summary>
/// <remarks>
/// <para>
/// The usage figures are fetched with the token in <c>.credentials.json</c>, but the account
/// identity used to come only from the <c>oauthAccount</c> block of <c>.claude.json</c>. That
/// file is rewritten by every Claude Code process on the machine, and the two can disagree:
/// on 2026-11-13 a Windows machine's <c>.claude.json</c> named a second account for ninety
/// minutes while the token still belonged to the first, so the first account's usage was
/// published under the second account's key. Asking the profile endpoint with the same token
/// that fetches the usage makes the key and the figures describe the same account.
/// </para>
/// <para>
/// One request per token, and only on a poll where the usage endpoint has just answered, so
/// the per-address rate limit described on <see cref="ClaudeApiClient"/> is not put at risk.
/// A failed lookup is retried no sooner than <see cref="RetryAfterFailure"/>; until it
/// succeeds the caller falls back to the file, exactly as before this class existed.
/// </para>
/// <para>
/// Only a SHA-256 fingerprint of the token is kept, in memory, to notice when it changes.
/// </para>
/// </remarks>
internal sealed class ClaudeTokenAccountKey
{
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);

    private string? _tokenFingerprint;
    private string? _accountKey;
    private DateTimeOffset? _retryAt;

    /// <summary>
    /// The key for the account behind <paramref name="credential"/>, or null when it is not
    /// known: no sign-in here, the live source did not answer this poll, or the lookup failed.
    /// </summary>
    /// <param name="liveSucceeded">
    /// True when the usage endpoint answered on this poll. A lookup is only attempted then.
    /// </param>
    public async Task<string?> ResolveAsync(
        ClaudeCredential? credential,
        bool liveSucceeded,
        Func<ClaudeApiClient> clientFactory,
        DateTimeOffset now)
    {
        if (credential is not { HasToken: true })
        {
            return null;
        }

        var fingerprint = Fingerprint(credential.AccessToken!);
        if (!string.Equals(fingerprint, _tokenFingerprint, StringComparison.Ordinal))
        {
            // A renewed or different sign-in. What was known about the old token says
            // nothing certain about this one, so it is asked again.
            _tokenFingerprint = fingerprint;
            _accountKey = null;
            _retryAt = null;
        }

        if (_accountKey is not null)
        {
            return _accountKey;
        }

        if (!liveSucceeded || (_retryAt is { } retryAt && now < retryAt))
        {
            return null;
        }

        using var client = clientFactory();
        _accountKey = await client.GetAccountKeyAsync(credential.AccessToken!).ConfigureAwait(false);
        _retryAt = _accountKey is null ? now + RetryAfterFailure : null;
        return _accountKey;
    }

    private static string Fingerprint(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
