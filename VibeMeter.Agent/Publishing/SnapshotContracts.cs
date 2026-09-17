using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeMeter.Agent.Publishing;

/// <summary>
/// Local mirror of the collection API's snapshot request contract
/// (Webcoda-App: Features/AiUsage/AiUsageContracts.cs). The API sets
/// <c>JsonUnmappedMemberHandling.Disallow</c>, so these records must carry
/// EXACTLY the documented fields and nothing else. Never serialise a VibeMeter
/// model directly — <see cref="ProviderUsage"/> has many more members and the
/// request would be rejected, leaking internals on the wire (VibeMeter's
/// <c>ProviderUsage</c>, for instance, carries many more members).
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SnapshotRequest(
    int SchemaVersion,
    DateTimeOffset ObservedAt,
    IReadOnlyList<ProviderSnapshot>? Providers)
{
    /// <summary>The API accepts schema version 1 only.</summary>
    public const int CurrentSchemaVersion = 1;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ProviderSnapshot(
    string? ProviderId,
    string? State,
    string? PlanLabel,
    IReadOnlyList<GaugeSnapshot>? Gauges);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GaugeSnapshot(
    string? Id,
    string? Title,
    string? Subtitle,
    decimal PercentRemaining,
    DateTimeOffset? ResetAt,
    int? ResetWindowSeconds);

/// <summary>
/// A mapped snapshot ready for the wire: the typed request, the exact JSON
/// document (the idempotency key is the hash of these bytes), and any
/// deterministic-mapping warnings worth surfacing in the log.
/// </summary>
public sealed record MappedSnapshot(
    SnapshotRequest Request,
    string Json,
    IReadOnlyList<string> Warnings);

/// <summary>
/// The one serialiser used for the wire document. It must stay deterministic —
/// camelCase, no indentation, nulls included — because a re-serialised
/// identical snapshot must produce byte-identical output for the
/// payload-derived idempotency key to be stable across retries.
/// </summary>
public static class SnapshotJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
}
