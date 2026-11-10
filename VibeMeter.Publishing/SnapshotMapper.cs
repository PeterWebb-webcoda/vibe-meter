using System.Text.Json;
using System.Text.RegularExpressions;
using VibeMeter.Core;

namespace VibeMeter.Publishing;

/// <summary>
/// Maps VibeMeter <see cref="ProviderUsage"/> reports onto the collection API's
/// snapshot contract. The authoritative rules live in the API's
/// the API's request validator (1..16 providers, 0..16 gauges, the fixed state
/// vocabulary, identifier pattern, paired reset fields) — this class mirrors
/// them so the agent never sends a request the server would reject.
/// Deterministic by design: identical inputs produce a byte-identical document,
/// which is what keeps the payload-derived idempotency key stable.
/// </summary>
public sealed partial class SnapshotMapper
{
    // Mirrors the API's provider and gauge count ceilings.
    public const int MaxProviders = 16;
    public const int MaxGauges = 16;

    // Mirrors the API's reset rules: resetWindowSeconds must be
    // between 60 seconds and 366 days, and resetAt must fall within
    // observedAt − 1 day .. observedAt + 366 days.
    public const int MinResetWindowSeconds = 60;
    public const int MaxResetWindowSeconds = 31_622_400;

    private const int MaxIdentifierLength = 64;
    private const int MaxTitleLength = 100;
    private const int MaxSubtitleLength = 200;
    private const int MaxPlanLabelLength = 100;

    [GeneratedRegex("[^a-z0-9._-]")]
    private static partial Regex InvalidIdentifierCharacters();

    // Deliberately broad: anything shaped like local@domain.tld. Over-matching a plan label
    // costs a word of display text; under-matching puts a person's address on the server.
    [GeneratedRegex(@"[A-Za-z0-9._%+'-]+@[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+")]
    private static partial Regex EmailAddress();

    // Separators left dangling once an email is cut out of "Tier — email".
    private static readonly char[] LabelSeparators = [' ', '—', '–', '-', '|', '·', ',', ':', '(', ')', '[', ']', '<', '>'];

    private readonly AccountLabels _accountLabels;

    /// <summary>A mapper that publishes no account labels: the pre-account-split document.</summary>
    public SnapshotMapper() : this(null) { }

    /// <param name="accountLabels">
    /// Which account label to publish per provider; null or <see cref="AccountLabels.None"/>
    /// emits no account fields at all.
    /// </param>
    public SnapshotMapper(AccountLabels? accountLabels)
    {
        _accountLabels = accountLabels ?? AccountLabels.None;
    }

    /// <summary>
    /// Maps a cycle's provider results onto the wire contract.
    /// <paramref name="observedAt"/> must be <see cref="DateTimeOffset.UtcNow"/>
    /// (UTC with zero offset — the API rejects any other offset).
    /// </summary>
    public MappedSnapshot Map(IReadOnlyList<ProviderUsage> usages, DateTimeOffset observedAt)
    {
        // Defensive: the API rejects any observedAt whose offset is not zero, and a
        // rejection here is a silent 400 on every cycle. Normalising costs nothing and
        // is a no-op for a caller that already passed UtcNow.
        observedAt = observedAt.ToUniversalTime();

        var warnings = new List<string>();

        // Too many providers: drop the tail (registry order is deterministic)
        // rather than fail the whole cycle.
        var selected = usages.Count > MaxProviders ? usages.Take(MaxProviders).ToList() : usages;
        if (usages.Count > MaxProviders)
        {
            warnings.Add($"Dropping the last {usages.Count - MaxProviders} provider report(s); the API accepts at most {MaxProviders}.");
        }

        var providerIds = new HashSet<string>(StringComparer.Ordinal);
        var providers = new List<ProviderSnapshot>();

        foreach (var usage in selected)
        {
            if (usage is null)
            {
                continue;
            }

            var providerId = SanitiseIdentifier(usage.ProviderId, $"provider-{providers.Count + 1}");
            if (providerId != usage.ProviderId?.Trim())
            {
                warnings.Add($"Provider id '{usage.ProviderId}' was normalised to '{providerId}' to satisfy the API's identifier pattern.");
            }

            if (!providerIds.Add(providerId))
            {
                warnings.Add($"Duplicate provider id '{providerId}' — keeping the first report and dropping the rest.");
                continue;
            }

            // No email address leaves the machine in a plan label. The Google provider puts
            // the account's address there for the card; the card keeps it, the wire does not.
            var planLabel = StripEmails(usage.PlanLabel, out var strippedEmail);

            var (accountLabel, accountKey) = MapAccount(usage, providerId, strippedEmail, warnings);

            providers.Add(new ProviderSnapshot(
                providerId,
                MapState(usage.State),
                CleanText(planLabel, MaxPlanLabelLength),
                MapGauges(usage.Gauges, providerId, observedAt, warnings),
                accountLabel,
                accountKey));
        }

        var request = new SnapshotRequest(SnapshotRequest.CurrentSchemaVersion, observedAt, providers);
        return new MappedSnapshot(
            request,
            JsonSerializer.Serialize(request, SnapshotJson.Options),
            warnings);
    }

    /// <summary>
    /// Decides the (accountLabel, accountKey) pair for one provider. Both are null — and so
    /// absent from the JSON — unless a label is configured for the provider: the key is
    /// never sent on its own. The key is the provider's own, or failing that one derived
    /// from an email the plan label carried (Google's only account identifier); with
    /// neither, the label goes alone and the API keys the account by the label.
    /// </summary>
    private (string? Label, string? Key) MapAccount(
        ProviderUsage usage,
        string providerId,
        string? strippedEmail,
        List<string> warnings)
    {
        var label = _accountLabels.For(providerId);
        if (label is null)
        {
            return (null, null);
        }

        var key = usage.AccountKey;
        if (key is not null && !AccountKey.IsValid(key))
        {
            // Never quote the value: it is meant to be a hash, but a provider bug could
            // have put something rawer here.
            warnings.Add($"Provider '{providerId}' reported a malformed account key; publishing its account label without one.");
            key = null;
        }

        key ??= strippedEmail is null ? null : AccountKey.Derive(providerId, strippedEmail);
        return (label, key);
    }

    /// <summary>
    /// Removes every email address from a plan label, tidying the separator it leaves
    /// behind (<c>"Gemini Pro — someone@example.com"</c> becomes <c>"Gemini Pro"</c>).
    /// <paramref name="firstEmail"/> receives the first address removed, so it can be
    /// HASHED into an account key; it must not be stored or logged.
    /// </summary>
    internal static string? StripEmails(string? label, out string? firstEmail)
    {
        firstEmail = null;
        if (string.IsNullOrEmpty(label))
        {
            return label;
        }

        var match = EmailAddress().Match(label);
        if (!match.Success)
        {
            return label;
        }

        firstEmail = match.Value;
        var kept = EmailAddress()
            .Split(label)
            .Select(part => part.Trim(LabelSeparators))
            .Where(part => part.Length > 0)
            .ToList();
        return kept.Count == 0 ? null : string.Join(" ", kept);
    }

    /// <summary>
    /// Maps VibeMeter's <see cref="ProviderState"/> onto the API's fixed
    /// vocabulary: available, not-configured, disabled, stale, error.
    /// </summary>
    private static string MapState(ProviderState state) => state switch
    {
        // Fresh, successful fetch — the only clean one-to-one mapping.
        ProviderState.Ok => "available",

        // Direct vocabulary matches.
        ProviderState.NotConfigured => "not-configured",
        ProviderState.Disabled => "disabled",
        ProviderState.Error => "error",

        // No clean mapping: "Loading" is a transient UI state that never
        // survives a completed FetchAsync in the agent (we await each fetch). If
        // it ever appears anyway, the provider's data is by definition not
        // current, so "stale" is the most honest of the five permitted states —
        // "error" would claim a failure that did not happen and "not-configured"
        // would misreport setup. Unknown future enum values get the same answer.
        _ => "stale",
    };

    private IReadOnlyList<GaugeSnapshot> MapGauges(
        IReadOnlyList<UsageGauge> gauges,
        string providerId,
        DateTimeOffset observedAt,
        List<string> warnings)
    {
        var mapped = new List<GaugeSnapshot>();
        var gaugeIds = new HashSet<string>(StringComparer.Ordinal);

        // Too many gauges: drop the tail (provider order is deterministic).
        var selected = gauges.Count > MaxGauges ? gauges.Take(MaxGauges).ToList() : gauges;
        if (gauges.Count > MaxGauges)
        {
            warnings.Add($"Provider '{providerId}' reported {gauges.Count} gauges; dropping the last {gauges.Count - MaxGauges} (API maximum is {MaxGauges}).");
        }

        foreach (var gauge in selected)
        {
            if (gauge is null)
            {
                continue;
            }

            var id = SanitiseIdentifier(gauge.Id, $"gauge-{mapped.Count + 1}");
            if (id != gauge.Id?.Trim())
            {
                warnings.Add($"Provider '{providerId}': gauge id '{gauge.Id}' was normalised to '{id}' to satisfy the API's identifier pattern.");
            }

            // Gauge ids must be unique within a provider; suffix deterministically.
            // The suffix must fit INSIDE the API's identifier limit, so trim the base
            // to make room rather than appending past it - an over-long id fails
            // validation for the whole snapshot, not just this gauge.
            var unique = id;
            for (var suffix = 2; !gaugeIds.Add(unique); suffix++)
            {
                var tail = $"-{suffix}";
                var baseId = id.Length + tail.Length > MaxIdentifierLength
                    ? id[..(MaxIdentifierLength - tail.Length)]
                    : id;
                unique = baseId + tail;
            }

            // The API accepts resetAt only together with resetWindowSeconds and
            // bounds both — see MapReset for the emission rule.
            var (resetAt, resetWindowSeconds) =
                MapReset(gauge.ResetAt, gauge.ResetWindowSeconds, observedAt);

            mapped.Add(new GaugeSnapshot(
                unique,
                // Title is required by the API; fall back to the gauge id.
                CleanText(gauge.Title, MaxTitleLength) ?? unique,
                CleanText(gauge.Subtitle, MaxSubtitleLength),
                Math.Clamp(gauge.PercentRemaining, 0, 100),
                resetAt,
                resetWindowSeconds));
        }

        return mapped;
    }

    /// <summary>
    /// Decides the (resetAt, resetWindowSeconds) pair for one gauge. The API
    /// accepts the two only as a pair — a lone half is rejected — and bounds
    /// each: the window must be 60 s..366 d, and resetAt must be UTC with a zero
    /// offset, within observedAt − 1 day .. observedAt + 366 days. When either
    /// value is missing or fails its bound, the WHOLE pair is omitted: clamping
    /// the window or trimming the timestamp would misreport a real provider
    /// reading, and an honestly blank reset ring beats a plausible lie.
    /// </summary>
    internal static (DateTimeOffset? ResetAt, int? ResetWindowSeconds) MapReset(
        DateTime? resetAt,
        int? resetWindowSeconds,
        DateTimeOffset observedAt)
    {
        if (resetAt is not { } reset
            || resetWindowSeconds is not { } window
            || window is < MinResetWindowSeconds or > MaxResetWindowSeconds)
        {
            return (null, null);
        }

        // Gauge timestamps are local DateTimes (each provider normalises to local
        // for the UI); the API demands UTC with a zero offset.
        DateTimeOffset utcReset;
        try
        {
            utcReset = new DateTimeOffset(reset.ToUniversalTime());
        }
        catch (ArgumentException)
        {
            // A timestamp that cannot even be represented in UTC is outside the
            // accepted range by definition.
            return (null, null);
        }

        if (utcReset < observedAt.AddDays(-1) || utcReset > observedAt.AddDays(366))
        {
            return (null, null);
        }

        return (utcReset, window);
    }

    /// <summary>
    /// Forces an identifier into the API's pattern
    /// (<c>^[a-z0-9][a-z0-9._-]*$</c>, max 64 chars). All current VibeMeter ids
    /// already conform; this is defensive against provider-constructed ids.
    /// </summary>
    private static string SanitiseIdentifier(string? candidate, string fallback)
    {
        var slug = InvalidIdentifierCharacters().Replace(
            (candidate ?? "").Trim().ToLowerInvariant(),
            "-");
        if (slug.Length == 0 || !char.IsAsciiLetterOrDigit(slug[0]))
        {
            slug = slug.Length == 0 ? fallback : "x-" + slug;
        }

        return slug.Length <= MaxIdentifierLength ? slug : slug[..MaxIdentifierLength];
    }

    private static string? CleanText(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.Length <= maxLength)
        {
            return value;
        }

        // Truncate without splitting a surrogate pair.
        var length = maxLength;
        if (char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }
}
