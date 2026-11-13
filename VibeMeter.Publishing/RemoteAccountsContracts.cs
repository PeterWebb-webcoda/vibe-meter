using System.Text.Json;

namespace VibeMeter.Publishing;

/// <summary>
/// The collection API's read-back of every account the signed-in user has published:
/// <c>GET {ApiBaseUrl}/api/v1/ai-usage/accounts</c>. Read-only and display-only — nothing
/// here is ever mapped back into a snapshot (see <see cref="RemoteAccount"/>).
/// </summary>
/// <remarks>
/// Unlike the snapshot contract, these records deliberately TOLERATE unknown members: the
/// endpoint may grow fields, and an older tray app must keep reading a newer API. Every
/// member is nullable for the same reason, so a missing field degrades one card rather
/// than failing the whole read.
/// </remarks>
public sealed record RemoteAccountsResponse(
    int? SchemaVersion,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt,
    IReadOnlyList<RemoteProviderAccounts>? Providers);

public sealed record RemoteProviderAccounts(
    string? ProviderId,
    IReadOnlyList<RemoteAccountReading>? Accounts);

public sealed record RemoteAccountReading(
    string? AccountId,
    string? AccountLabel,
    string? State,
    string? PlanLabel,
    IReadOnlyList<RemoteGauge>? Gauges,
    DateTimeOffset? ObservedAt,
    DateTimeOffset? ReceivedAt);

/// <param name="PercentRemaining">
/// 0–100 REMAINING, the same sense as <see cref="VibeMeter.Core.UsageGauge.PercentRemaining"/>
/// and <see cref="GaugeSnapshot.PercentRemaining"/>: the publisher sends the app's own
/// figure unchanged, so it is read back unchanged — no inversion either way.
/// </param>
public sealed record RemoteGauge(
    string? Id,
    string? Title,
    string? Subtitle,
    decimal? PercentRemaining,
    DateTimeOffset? ResetAt,
    int? ResetWindowSeconds);

/// <summary>
/// One account belonging to ANOTHER machine, chosen for display by
/// <see cref="RemoteAccountSelection"/>. Deliberately its own type rather than a
/// <see cref="VibeMeter.Core.ProviderUsage"/>: the publish path only ever accepts
/// <c>ProviderUsage</c>, so a remote reading cannot be handed to it by mistake and
/// published back as though this machine had observed it.
/// </summary>
/// <param name="ObservedAt">
/// When the other machine observed the reading (falling back to when the API received it),
/// or null when the response carried neither — which <see cref="RemoteAccountFreshness"/>
/// treats as too old to show.
/// </param>
public sealed record RemoteAccount(
    string ProviderId,
    string AccountLabel,
    string? State,
    string? PlanLabel,
    IReadOnlyList<RemoteGauge> Gauges,
    DateTimeOffset? ObservedAt);

/// <summary>The serialiser options for reading the accounts endpoint.</summary>
internal static class RemoteAccountsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,

        // Explicit, although it is the default: the read-back must ignore members it
        // does not know, the opposite of the snapshot contract's Disallow.
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
    };
}
