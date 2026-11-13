namespace VibeMeter.Publishing;

/// <summary>
/// Decides which accounts in the read-back belong to ANOTHER machine and are worth a card
/// here.
/// </summary>
/// <remarks>
/// <para>
/// An account is someone else's only when it carries a label AND that label differs from
/// the one this machine publishes for the provider. Two consequences follow, both
/// deliberate:
/// </para>
/// <list type="bullet">
/// <item>An unlabelled (legacy) account is never shown: it could be this machine's own
/// pre-label reading, and showing it would put this machine beside itself.</item>
/// <item>A provider this machine publishes WITHOUT a label shows nothing at all, because
/// with no label of its own this machine cannot tell which account is its own.</item>
/// </list>
/// </remarks>
public static class RemoteAccountSelection
{
    /// <summary>
    /// The providers whose other accounts are shown. Claude only for now — the case this
    /// was built for — and the one place to widen it.
    /// </summary>
    public static readonly IReadOnlySet<string> SupportedProviders =
        new HashSet<string>(StringComparer.Ordinal) { "claude" };

    /// <summary>Mirrors the snapshot contract's per-provider gauge ceiling.</summary>
    private const int MaxGauges = SnapshotMapper.MaxGauges;

    public static IReadOnlyList<RemoteAccount> Select(RemoteAccountsResponse? response, AccountLabels localLabels)
    {
        var selected = new List<RemoteAccount>();
        if (response?.Providers is null)
        {
            return selected;
        }

        var seen = new HashSet<(string Provider, string Label)>();

        foreach (var provider in response.Providers)
        {
            var providerId = provider?.ProviderId?.Trim().ToLowerInvariant();
            if (providerId is null || !SupportedProviders.Contains(providerId) || provider!.Accounts is null)
            {
                continue;
            }

            if (localLabels.For(providerId) is not { } ownLabel)
            {
                continue;
            }

            foreach (var account in provider.Accounts)
            {
                var label = account?.AccountLabel?.Trim();

                // A label the API itself would not accept is not one to display either.
                if (account is null || !AccountLabels.IsValid(label) || label == ownLabel)
                {
                    continue;
                }

                // The other machine has switched this provider off, or never set it up:
                // there is no reading to show, only a card that would look broken.
                if (account.State is "disabled" or "not-configured")
                {
                    continue;
                }

                if (!seen.Add((providerId, label!)))
                {
                    continue;
                }

                selected.Add(new RemoteAccount(
                    providerId,
                    label!,
                    account.State,
                    account.PlanLabel,
                    (account.Gauges ?? []).Where(gauge => gauge is not null).Take(MaxGauges).ToList(),
                    account.ObservedAt ?? account.ReceivedAt));
            }
        }

        return selected;
    }
}

/// <summary>How old a remote reading is, for display.</summary>
public enum RemoteAccountAge
{
    /// <summary>Recent enough to show as current.</summary>
    Fresh,

    /// <summary>Shown, but marked as old.</summary>
    Stale,

    /// <summary>Too old to show at all.</summary>
    Expired,
}

public static class RemoteAccountFreshness
{
    /// <summary>
    /// Past this a remote reading is marked stale. The same 20 minutes after which a
    /// publishing host stops sending a provider's figures at all
    /// (<see cref="DesktopPublishHost.DefaultStalenessThreshold"/>), so a card turns stale
    /// at the point the other machine would itself have stopped vouching for it.
    /// </summary>
    public static readonly TimeSpan StaleAfter = DesktopPublishHost.DefaultStalenessThreshold;

    /// <summary>Past this a remote reading is not shown at all.</summary>
    public static readonly TimeSpan ExpireAfter = TimeSpan.FromHours(24);

    /// <summary>
    /// Classifies a reading observed at <paramref name="observedAt"/>. A reading with no
    /// time at all cannot be vouched for and is expired; one stamped slightly in the
    /// future (clock skew between machines) is fresh.
    /// </summary>
    public static RemoteAccountAge Classify(DateTimeOffset? observedAt, DateTimeOffset now)
    {
        if (observedAt is not { } observed)
        {
            return RemoteAccountAge.Expired;
        }

        var age = now - observed;
        if (age > ExpireAfter)
        {
            return RemoteAccountAge.Expired;
        }

        return age > StaleAfter ? RemoteAccountAge.Stale : RemoteAccountAge.Fresh;
    }
}

/// <summary>
/// Keeps the latest selection of other machines' accounts, refreshing it at most once per
/// <see cref="RefreshInterval"/> and backing off after a failure.
/// </summary>
/// <remarks>
/// <para>
/// It owns no timer: the host calls <see cref="RefreshIfDueAsync"/> after each publish
/// attempt, and the call is a no-op until a read is due. A failed read keeps the last
/// good selection, so a blip at the API never blanks a card that was showing.
/// </para>
/// <para>
/// Not thread-safe, and need not be: the desktop host runs at most one cycle at a time.
/// <see cref="Current"/> is replaced, never mutated, so a reader on another thread always
/// sees one whole list.
/// </para>
/// </remarks>
public sealed class RemoteAccountsRefresher
{
    /// <summary>The fastest this ever reads: once every five minutes.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The slowest it backs off to, and how long it waits after a 404 — an API that does
    /// not have the endpoint will not grow one in the next few minutes.
    /// </summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly IRemoteAccountsReader _reader;
    private readonly AccountLabels _localLabels;
    private readonly IPublishLog _log;
    private readonly TimeProvider _time;

    private DateTimeOffset? _nextDue;
    private int _consecutiveFailures;
    private bool _problemReported;

    public RemoteAccountsRefresher(
        IRemoteAccountsReader reader,
        AccountLabels localLabels,
        IPublishLog log,
        TimeProvider? time = null)
    {
        _reader = reader;
        _localLabels = localLabels;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The latest good selection; empty until the first successful read.</summary>
    public IReadOnlyList<RemoteAccount> Current { get; private set; } = [];

    /// <summary>When the next read may happen, or null when one is due now. A test seam.</summary>
    internal DateTimeOffset? NextDue => _nextDue;

    /// <summary>
    /// Reads the endpoint if a read is due. Returns true when <see cref="Current"/> was
    /// replaced by a successful read; false when nothing was due or the read failed (in
    /// which case <see cref="Current"/> is unchanged). Never throws bar cancellation —
    /// the reader does not.
    /// </summary>
    public async Task<bool> RefreshIfDueAsync(CancellationToken cancellationToken)
    {
        if (_nextDue is { } due && _time.GetUtcNow() < due)
        {
            return false;
        }

        var result = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();

        if (result.Succeeded)
        {
            Current = RemoteAccountSelection.Select(result.Response, _localLabels);
            _consecutiveFailures = 0;
            _nextDue = now + RefreshInterval;

            if (_problemReported)
            {
                _problemReported = false;
                _log.Info("Other machines' accounts can be read from the collection API again.");
            }

            return true;
        }

        _consecutiveFailures++;
        var wait = result.Status == RemoteAccountsReadStatus.NotSupported
            ? MaxBackoff
            : BackoffAfter(_consecutiveFailures);
        _nextDue = now + wait;

        // Once per run of failures, not once per attempt: this is a display nicety, and a
        // log line every few minutes for an API that predates the endpoint is noise.
        if (!_problemReported)
        {
            _problemReported = true;
            _log.Info(DescribeFailure(result) + $" Keeping what was last shown; trying again in {wait.TotalMinutes:0} min.");
        }

        return false;
    }

    /// <summary>
    /// The wait after <paramref name="consecutiveFailures"/> failed reads in a row: the
    /// refresh interval doubled per failure (10, 20, 40 min), capped at
    /// <see cref="MaxBackoff"/>.
    /// </summary>
    internal static TimeSpan BackoffAfter(int consecutiveFailures)
    {
        var doublings = Math.Clamp(consecutiveFailures, 0, 10);
        var wait = TimeSpan.FromTicks(RefreshInterval.Ticks << doublings);
        return wait > MaxBackoff ? MaxBackoff : wait;
    }

    // Status codes only: never a body, never a token.
    private static string DescribeFailure(RemoteAccountsReadResult result) => result.Status switch
    {
        RemoteAccountsReadStatus.NotSupported =>
            "The collection API does not offer other machines' accounts (HTTP 404); it may predate the feature.",
        RemoteAccountsReadStatus.AuthFailure => result.StatusCode is { } code
            ? $"Reading other machines' accounts was refused (HTTP {code})."
            : "Reading other machines' accounts needs a signed-in publisher; none is available yet.",
        _ => result.StatusCode is { } status
            ? $"Reading other machines' accounts failed (HTTP {status})."
            : "Reading other machines' accounts failed (no usable response).",
    };
}
