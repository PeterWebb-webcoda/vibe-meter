using System.Diagnostics.CodeAnalysis;
using VibeMeter.Core;
using VibeMeter.Publishing.AccessToken;

namespace VibeMeter.Publishing;

/// <summary>
/// What an interactive desktop host needs before it may publish at all. Every
/// value comes from the user's own settings file; none is defaulted in source,
/// for the reason <see cref="DeviceCodeAuthOptions"/> gives — the client,
/// tenant and scope identifiers are not secrets, but baking one organisation's
/// directory into this repository would tie a general tool to that tenant.
/// </summary>
/// <param name="Enabled">
/// The opt-in. Off by default and checked FIRST, before anything at all is
/// built: a host that is not publishing must not create an offline queue, a
/// token cache or an HTTP client, let alone reach the network.
/// </param>
public sealed record DesktopPublishSettings(
    bool Enabled,
    string? ApiBaseUrl,
    string? ClientId,
    string? TenantId,
    string? Scope)
{
    /// <summary>
    /// Validates the settings, reporting every problem at once rather than one
    /// per attempt — a person filling in four fields should not have to save
    /// four times to discover four mistakes.
    /// </summary>
    /// <remarks>
    /// Says nothing about <see cref="Enabled"/>: "switched off" is not a
    /// problem to report, so the caller checks that separately.
    /// </remarks>
    public bool TryResolve([NotNullWhen(true)] out Uri? apiBaseUrl, out IReadOnlyList<string> problems)
    {
        apiBaseUrl = null;
        var found = new List<string>();

        // Trimmed and de-slashed exactly as AgentConfig does it, so the two
        // hosts cannot end up POSTing to subtly different URLs from the same
        // text.
        var rawBaseUrl = ApiBaseUrl?.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            found.Add("the collection API base URL is missing");
        }
        else if (!Uri.TryCreate(rawBaseUrl, UriKind.Absolute, out var parsed)
                 || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            found.Add($"the collection API base URL must be an absolute http(s) URL (got '{rawBaseUrl}')");
        }
        else
        {
            apiBaseUrl = parsed;
        }

        if (string.IsNullOrWhiteSpace(ClientId))
        {
            found.Add("the Entra application (client) id is missing");
        }

        if (string.IsNullOrWhiteSpace(TenantId))
        {
            found.Add("the Entra directory (tenant) id is missing");
        }

        if (string.IsNullOrWhiteSpace(Scope))
        {
            found.Add("the delegated scope is missing (for example api://<api-app-id>/Usage.Write)");
        }

        problems = found;

        // The null check is redundant with found.Count - a missing or malformed
        // URL always adds a problem - but it is what lets the compiler prove the
        // NotNullWhen(true) contract rather than take it on trust.
        if (found.Count > 0 || apiBaseUrl is null)
        {
            apiBaseUrl = null;
            return false;
        }

        return true;
    }
}

/// <summary>
/// The publishing half of an interactive desktop host (the WPF tray app), run
/// DETACHED from whatever refreshed the UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why detached is the whole point.</b> The tray app's refresh is driven by
/// a <c>DispatcherTimer</c> whose Tick is an async-void handler on the UI
/// thread, and its refresh captures the dispatcher on every continuation. Doing
/// the publish inline would therefore (a) freeze the window for as long as the
/// publish takes — up to three HTTP attempts of 30 s each plus backoff — (b)
/// put the offline queue's synchronous file writes on the dispatcher, and (c)
/// let any escaping exception travel out through the async-void Tick handler,
/// where it is an unhandled exception that tears the process down. So the
/// caller gets <see cref="Publish"/>: it hands the work to the thread pool and
/// returns immediately, and NOTHING that happens afterwards can reach it.
/// </para>
/// <para>
/// <b>Why a refusal, not a queue, when one is already running.</b> A publish
/// can outlast several refreshes, and two overlapping ones would race the
/// offline queue and the publish policy. Lining them up instead would be worse
/// than dropping them: each waiting entry holds figures that were already stale
/// when it was queued, and the next refresh is only a minute away with better
/// ones. So a refresh that arrives mid-publish is declined — the reading is
/// delayed by one cycle, never corrupted.
/// </para>
/// </remarks>
public sealed class DesktopPublishHost : IDisposable
{
    /// <summary>
    /// How old a provider's underlying data may be before it is omitted.
    /// Deliberately the same 20 minutes as <c>AgentConfig.DefaultStalenessThreshold</c>:
    /// both hosts publish into one merged per-provider view, so a machine's
    /// readings must age out at the same rate whichever of them sent them.
    /// </summary>
    public static readonly TimeSpan DefaultStalenessThreshold = TimeSpan.FromMinutes(20);

    /// <summary>
    /// How long <see cref="Dispose"/> gives an in-flight publish to notice the
    /// cancellation and unwind. Cancelling aborts the HTTP request and any
    /// backoff delay immediately, so this is a safety net rather than a wait —
    /// but it is bounded, because it runs on the UI thread while the app is
    /// closing and a hang there looks exactly like a crash.
    /// </summary>
    private static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(2);

    private readonly SnapshotPublishCycle _cycle;
    private readonly IPublishLog _log;
    private readonly IDisposable? _publisherLifetime;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Guards <see cref="_current"/>. Held only long enough to look at the
    /// previous task and schedule the next one, so the UI thread never waits on
    /// it for anything measurable.
    /// </summary>
    private readonly object _gate = new();

    private Task _current = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// Composes a host around an already-built pipeline. The sanctioned entry
    /// point is <see cref="TryCreate"/>; this exists so a test can drive the
    /// detachment rules against a fake publisher with no network, no MSAL and
    /// no settings.
    /// </summary>
    /// <param name="publisherLifetime">
    /// Anything the host must dispose with itself — in practice the
    /// <see cref="SnapshotPublisher"/> and its <c>HttpClient</c>.
    /// </param>
    internal DesktopPublishHost(SnapshotPublishCycle cycle, IPublishLog log, IDisposable? publisherLifetime = null)
    {
        _cycle = cycle;
        _log = log;
        _publisherLifetime = publisherLifetime;
    }

    /// <summary>
    /// Builds the host, or returns <see langword="null"/> when this machine is
    /// not publishing — because the user has not opted in, or because the
    /// settings are incomplete (in which case the reasons are logged, since a
    /// feature that silently does nothing is indistinguishable from a broken
    /// one).
    /// </summary>
    /// <param name="queueDirectory">
    /// THIS host's offline queue, which must not be the agent's. Queue entries
    /// are coordinated by file name with no cross-process locking, so two hosts
    /// sharing a directory would race each other's flushes — see
    /// <see cref="OfflineQueue"/>.
    /// </param>
    /// <param name="tokenCacheDirectory">
    /// Where MSAL keeps the cached credential. Unlike the queue this one MAY be
    /// shared with the agent: the cache helper takes a cross-process lock, and
    /// sharing it means one sign-in serves both hosts on the machine.
    /// </param>
    /// <param name="notifySignIn">
    /// Where the device-code message is shown. It arrives on the publishing
    /// thread, and it carries a verification URL and a user code but never a
    /// token, so it is safe to display — see
    /// <see cref="DeviceCodeAccessTokenProvider"/>.
    /// </param>
    public static DesktopPublishHost? TryCreate(
        DesktopPublishSettings settings,
        string queueDirectory,
        string tokenCacheDirectory,
        IPublishLog log,
        Action<string> notifySignIn,
        TimeSpan? stalenessThreshold = null)
    {
        // The opt-in, checked before anything is constructed: a host that is
        // switched off leaves no queue directory, no token cache and no socket
        // behind it.
        if (!settings.Enabled)
        {
            return null;
        }

        if (!settings.TryResolve(out var apiBaseUrl, out var problems))
        {
            log.Error(
                "Publishing is switched on but not configured, so nothing will be published: "
                + string.Join("; ", problems) + ".");
            return null;
        }

        var queue = new OfflineQueue(queueDirectory);

        // Interactive sign-in is allowed here — unlike the daemon, this host has
        // a person in front of it. It is never triggered at startup: the first
        // token is asked for by the first publish, which already runs detached.
        var tokens = new DeviceCodeAccessTokenProvider(
            new DeviceCodeAuthOptions(settings.ClientId!, settings.TenantId!, settings.Scope!, tokenCacheDirectory),
            allowInteractive: true,
            notifySignIn);

        var publisher = new SnapshotPublisher(apiBaseUrl, tokens);

        return new DesktopPublishHost(
            new SnapshotPublishCycle(
                new SnapshotComposer(new SnapshotMapper(), stalenessThreshold ?? DefaultStalenessThreshold, log),
                publisher,
                queue,
                log,
                CreatePolicyFor(queue)),
            log,
            publisher);
    }

    /// <summary>
    /// The publish policy this host runs under: a baseline that OUTLIVES the
    /// host, plus one startup publish.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why the state is on disk and not in memory.</b> This host does not
    /// live as long as the application does. <c>App.RestartPublishing</c>
    /// disposes and rebuilds it at startup and again every time the settings are
    /// saved, so a policy holding its state in memory loses its baseline on each
    /// rebuild, reads back "nothing published yet" and takes the FirstPublish
    /// branch — publishing regardless of the minimum interval. Observed in
    /// production as three rows inside two minutes against a 290-second floor.
    /// The state therefore lives in the offline-queue directory this host
    /// already owns (see <see cref="FilePublishPolicyStore"/> for why there and
    /// nowhere else), where a rebuilt host reads back what the previous one
    /// wrote and the floor means what it says.
    /// </para>
    /// <para>
    /// <b>Why a restart still publishes.</b> Persisting the baseline would
    /// otherwise make a relaunch silent for up to a heartbeat, which is exactly
    /// how it would look to someone who had just switched publishing on. So the
    /// policy is given one startup publish — spent once, and only once the
    /// minimum interval has elapsed, so a run of restarts or saved settings
    /// costs at most one row per interval rather than one row apiece.
    /// </para>
    /// </remarks>
    internal static PublishPolicy CreatePolicyFor(OfflineQueue queue) =>
        new(FilePublishPolicyStore.ForQueue(queue), options: null, publishOnStart: true);

    /// <summary>
    /// Hands one refresh's reports to the publish cycle and returns at once —
    /// the caller must NOT await the work, and there is nothing here to await.
    /// Returns <see langword="false"/> when nothing was started: an earlier
    /// publish is still in flight, or the host is shutting down.
    /// </summary>
    /// <remarks>
    /// Never throws. Everything the cycle can fail at — the network, the disk,
    /// sign-in, a rogue provider report — is caught and logged on the
    /// background task, because the caller is a UI refresh whose exceptions
    /// would surface through an async-void timer handler and end the process.
    /// </remarks>
    public bool Publish(IReadOnlyList<ProviderUsage> collected)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (!_current.IsCompleted)
            {
                _log.Info(
                    "A publish from an earlier refresh is still in flight; this refresh's figures are not being "
                    + "sent. The next refresh publishes the newer reading instead.");
                return false;
            }

            // Task.Run deliberately: it schedules onto the thread pool, where
            // there is no SynchronizationContext, so not one continuation below
            // can find its way back onto the dispatcher.
            _current = Task.Run(() => RunAsync(collected));
            return true;
        }
    }

    /// <summary>
    /// The in-flight publish, or a completed task when the host is idle. A test
    /// seam: the production caller must never wait on this, which is the whole
    /// point of the class.
    /// </summary>
    internal Task Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>
    /// Cancels anything in flight and releases the HTTP client. Bounded by
    /// <see cref="ShutdownGrace"/> so closing the app can never hang on a
    /// publish. Whatever the cancelled cycle had not yet sent is already on
    /// disk in the offline queue, so the next run picks it up.
    /// </summary>
    public void Dispose()
    {
        Task inFlight;
        lock (_gate)
        {
            // Closes the door first, so nothing can start between reading the
            // in-flight task and cancelling it.
            _disposed = true;
            inFlight = _current;
        }

        // Outside the lock: Cancel runs its registered callbacks (the HTTP
        // request's abort, among others) synchronously on this thread, and
        // running unknown callbacks while holding the gate is how a shutdown
        // turns into a deadlock.
        _shutdown.Cancel();

        try
        {
            // RunAsync swallows everything, so this can only time out, never throw.
            inFlight.Wait(ShutdownGrace);
        }
        catch (Exception)
        {
            // Shutdown is best-effort; a publish that will not let go is not
            // worth failing an app exit over.
        }

        // The CancellationTokenSource is deliberately NOT disposed: a detached
        // cycle that outlived the grace above still holds its token, and
        // disposing it under that cycle turns a clean cancellation into an
        // ObjectDisposedException. It owns no timer and no unmanaged handle, so
        // letting it be collected costs nothing - and leaving it alive also
        // makes a second Dispose harmless.
        _publisherLifetime?.Dispose();
    }

    /// <summary>
    /// The detached work: drain whatever an earlier outage left queued, then
    /// publish this cycle. Flushing first matches the agent and keeps the
    /// queue moving even on a machine whose figures never change enough for the
    /// policy to publish.
    /// </summary>
    private async Task RunAsync(IReadOnlyList<ProviderUsage> collected)
    {
        try
        {
            await _cycle.FlushQueueAsync(_shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // A stuck queue must not cost this cycle its publish.
            _log.Error($"Offline-queue flush failed: {ex.Message}");
        }

        try
        {
            await _cycle.PublishAsync(collected, _shutdown.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // The app is closing mid-publish; the snapshot stays queued if it
            // got that far, and the next run flushes it.
        }
        catch (Exception ex)
        {
            // The last line of defence. SnapshotPublishCycle already handles
            // every failure it anticipates, so reaching here means something
            // unanticipated — which is precisely what must not be allowed to
            // travel back up into a UI refresh.
            _log.Error($"Publishing failed unexpectedly: {ex.Message}");
        }
    }
}
