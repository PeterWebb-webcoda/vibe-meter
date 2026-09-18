using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeMeter.Publishing;

/// <summary>
/// "Have the numbers actually changed?" — a hash of the mapped snapshot with
/// <see cref="SnapshotRequest.ObservedAt"/> DELIBERATELY excluded.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not the idempotency key, and why the key must not become
/// this.</b> <see cref="IdempotencyKey"/> hashes the exact document bytes,
/// <c>observedAt</c> included, so it changes every cycle even when every figure
/// is identical — which is precisely what makes it a safe idempotency key: the
/// API treats a repeated key carrying a DIFFERENT body as a conflict, so a key
/// that ignored <c>observedAt</c> would let two genuinely different documents
/// (the same quota figures observed an hour apart, say) collide on one key and
/// be refused. The key therefore stays exactly as it is. This fingerprint is a
/// separate, purely LOCAL value: it never goes on the wire, never appears in a
/// header, and is only ever compared with the previous cycle's fingerprint to
/// answer "is there anything new to say?".
/// </para>
/// <para>
/// The prefix keeps the two apart at a glance in a log or a state file
/// (<c>f1-</c> here, <c>v1-</c> for the idempotency key) and leaves room to
/// change what is hashed later without a stored fingerprint from an older build
/// silently comparing equal to a new one.
/// </para>
/// <para>
/// Everything else in the document is in scope: schema version, the provider
/// list and its order, each provider's state and plan label, and every gauge's
/// id, titles, percentage and reset pair. A provider appearing or disappearing,
/// a percentage moving, or a reset time rolling over therefore all change the
/// fingerprint — the reset pair especially, because a window rolling over is
/// real news even when the percentage happens to land on its previous value.
/// </para>
/// </remarks>
public static class ContentFingerprint
{
    /// <summary>
    /// The value <c>observedAt</c> is replaced with before hashing. Any fixed
    /// instant would do; the Unix epoch is the one that is obviously a sentinel
    /// rather than a plausible reading if it ever shows up in a diff.
    /// </summary>
    private static readonly DateTimeOffset ObservedAtSentinel = DateTimeOffset.UnixEpoch;

    /// <summary>
    /// Fingerprints a mapped snapshot. Deterministic: the same figures always
    /// produce the same value, whatever time they were mapped at, because the
    /// document is re-serialised with the SAME serialiser the wire document
    /// uses (so field naming, ordering and null handling cannot drift apart
    /// from it) and only <c>observedAt</c> is substituted.
    /// </summary>
    public static string For(MappedSnapshot snapshot) => For(snapshot.Request);

    /// <inheritdoc cref="For(MappedSnapshot)"/>
    public static string For(SnapshotRequest request)
    {
        var withoutObservedAt = request with { ObservedAt = ObservedAtSentinel };
        var json = JsonSerializer.Serialize(withoutObservedAt, SnapshotJson.Options);
        return "f1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
    }
}
