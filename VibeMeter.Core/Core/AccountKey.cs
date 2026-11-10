using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VibeMeter.Core;

/// <summary>
/// Derives the collection API's <c>accountKey</c>: a stable, non-reversible key for the
/// provider account a reading belongs to, so two machines signed in to DIFFERENT accounts of
/// the same provider (a work Claude and a personal Claude, say) are kept apart rather than
/// overwriting each other's row.
/// </summary>
/// <remarks>
/// <para>
/// SHA-256 over a fixed application salt, the provider id and the provider's own account
/// identifier(s), lowercase hex, first 16 characters. The salt keeps the key from being a
/// bare hash of a well-known UUID; the provider id keeps one identifier from producing the
/// same key under two providers.
/// </para>
/// <para>
/// SECURITY: the raw identifiers (account and organisation UUIDs, an email) go in and only
/// the truncated hash comes out. Callers must hash at the point they read the identifier and
/// never carry, log, persist or publish the raw value.
/// </para>
/// </remarks>
public static partial class AccountKey
{
    /// <summary>
    /// Fixed for the life of the wire contract: changing it re-keys every account, and the
    /// API would see each one as a new account.
    /// </summary>
    internal const string Salt = "vibemeter/account-key/v1";

    /// <summary>The key length the API accepts.</summary>
    public const int Length = 16;

    [GeneratedRegex("^[0-9a-f]{16}$")]
    private static partial Regex ValidPattern();

    /// <summary>True when <paramref name="key"/> has the shape the API accepts.</summary>
    public static bool IsValid(string? key) => key is not null && ValidPattern().IsMatch(key);

    /// <summary>
    /// The key for one provider account, or <see langword="null"/> when no identifier is
    /// available — in which case the field is simply omitted and the API falls back to the
    /// account label. Blank identifiers are ignored; identifiers are trimmed and compared
    /// case-insensitively (UUIDs and emails are not case-significant).
    /// </summary>
    public static string? Derive(string providerId, params string?[] identifiers)
    {
        var parts = identifiers
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim().ToLowerInvariant())
            .ToArray();
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(providerId))
        {
            return null;
        }

        // Newline-separated so ("ab", "c") and ("a", "bc") cannot collide.
        var input = string.Join('\n', new[] { Salt, providerId.Trim().ToLowerInvariant() }.Concat(parts));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hash)[..Length];
    }
}
