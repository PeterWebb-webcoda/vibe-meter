using System.Text.RegularExpressions;

namespace VibeMeter.Publishing;

/// <summary>
/// Which account label this machine publishes for each provider: a per-machine default plus
/// optional per-provider overrides — e.g. default <c>webcoda</c>, with <c>codex</c> overridden
/// to <c>shared</c>. The collection API keys each provider's latest state by
/// (user, provider, account), so two machines signed in to different accounts of one
/// provider stop overwriting each other once each says which account it is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Unconfigured is the default and means "send nothing new".</b> With no label for a
/// provider, neither <c>accountLabel</c> nor <c>accountKey</c> is emitted, so the document
/// is byte-for-byte what it was before this existed — which matters because the API in
/// service before the account split rejects unknown members with a 400.
/// </para>
/// <para>
/// <b>An invalid label is ignored, never fatal.</b> A label must match
/// <c>^[a-z][a-z0-9-]{0,31}$</c>; anything else is dropped with a warning in
/// <see cref="Warnings"/> for the host to log, and that provider publishes unlabelled
/// rather than not at all.
/// </para>
/// </remarks>
public sealed partial class AccountLabels
{
    /// <summary>No labels at all: every provider publishes exactly as before.</summary>
    public static readonly AccountLabels None = new(null, new Dictionary<string, string>(), []);

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex ValidLabel();

    private readonly IReadOnlyDictionary<string, string> _overrides;

    private AccountLabels(string? defaultLabel, IReadOnlyDictionary<string, string> overrides, IReadOnlyList<string> warnings)
    {
        Default = defaultLabel;
        _overrides = overrides;
        Warnings = warnings;
    }

    /// <summary>The label for any provider without an override, or null.</summary>
    public string? Default { get; }

    /// <summary>Per-provider overrides, keyed by provider id (lowercase).</summary>
    public IReadOnlyDictionary<string, string> Overrides => _overrides;

    /// <summary>What was ignored while reading the configuration, for the host to log once.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>True when <paramref name="label"/> is a label the API accepts.</summary>
    public static bool IsValid(string? label) => label is not null && ValidLabel().IsMatch(label);

    /// <summary>The label for <paramref name="providerId"/>, or null when none is configured.</summary>
    public string? For(string providerId) =>
        _overrides.TryGetValue(providerId.Trim().ToLowerInvariant(), out var label) ? label : Default;

    /// <summary>
    /// Builds the labels from a default and a map of overrides, as the tray app's settings
    /// file holds them. Blank values mean "not configured"; invalid ones are ignored with a
    /// warning.
    /// </summary>
    public static AccountLabels Create(string? defaultLabel, IEnumerable<KeyValuePair<string, string>>? overrides)
    {
        var warnings = new List<string>();
        var validDefault = Validate(defaultLabel, "the default account label", warnings);

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rawProvider, rawLabel) in overrides ?? [])
        {
            var provider = rawProvider?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(provider))
            {
                warnings.Add("Ignoring an account-label override with no provider id.");
                continue;
            }

            if (Validate(rawLabel, $"the account label for '{provider}'", warnings) is { } label)
            {
                map[provider] = label;
            }
        }

        return new AccountLabels(validDefault, map, warnings);
    }

    /// <summary>
    /// Builds the labels from the agent's environment-variable form: a default, and overrides
    /// written <c>provider=label</c>, separated by commas or semicolons
    /// (<c>codex=shared,google=personal</c>).
    /// </summary>
    public static AccountLabels Parse(string? defaultLabel, string? overridesSpec)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        var warnings = new List<string>();

        foreach (var entry in (overridesSpec ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = entry.IndexOf('=');
            if (equals <= 0)
            {
                warnings.Add($"Ignoring account-label override '{entry}': expected provider=label.");
                continue;
            }

            pairs.Add(new(entry[..equals], entry[(equals + 1)..]));
        }

        var labels = Create(defaultLabel, pairs);
        return warnings.Count == 0
            ? labels
            : new AccountLabels(labels.Default, labels._overrides, [.. warnings, .. labels.Warnings]);
    }

    private static string? Validate(string? raw, string what, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var label = raw.Trim();
        if (IsValid(label))
        {
            return label;
        }

        warnings.Add(
            $"Ignoring {what} '{label}': it must be 1-32 characters of lowercase letters, digits and hyphens, "
            + "starting with a letter. Publishing continues without it.");
        return null;
    }
}
