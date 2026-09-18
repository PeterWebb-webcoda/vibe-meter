using System.Runtime.InteropServices;
using VibeMeter.Agent;
using VibeMeter.Agent.AccessToken;
using VibeMeter.Core;
using VibeMeter.Publishing;
using VibeMeter.Publishing.AccessToken;
using VibeMeter.Providers.Claude;
using VibeMeter.Providers.Codex;
using VibeMeter.Providers.Google;
using VibeMeter.Providers.Zai;

namespace VibeMeter.Agent;

/// <summary>
/// Headless VibeMeter agent. With no arguments it runs the unattended daemon:
/// collecting usage from every provider, mapping it onto the collection API's
/// snapshot contract, and POSTing it on a timer — queueing snapshots for later
/// whenever the API is unreachable. <c>--once</c> runs a single cycle and
/// exits; <c>--dry-run</c> collects and prints what would be published without
/// needing a token or sending anything; <c>--help</c> lists the modes.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        // Parse before touching configuration so a mistyped flag is a usage
        // error even on a machine with no VIBEMETER_* variables set - and so
        // an unknown argument can never fall through to the daemon loop.
        var options = CliOptions.Parse(args);
        switch (options.Mode)
        {
            case AgentLaunchMode.Help:
                Console.Out.WriteLine(CliOptions.HelpText);
                return 0;

            case AgentLaunchMode.UsageError:
                Console.Error.WriteLine(options.UsageError);
                Console.Error.WriteLine("Run with --help to see the accepted modes.");
                return 2;

            case AgentLaunchMode.Login:
                return await RunLoginAsync();

            case AgentLaunchMode.DryRun:
                return await RunDryRunAsync();

            case AgentLaunchMode.Daemon:
            case AgentLaunchMode.Once:
                return await RunConfiguredAsync(options.Mode);

            default:
                return 2;
        }
    }

    /// <summary>
    /// Interactive sign-in. Deliberately its own mode: the daemon must never
    /// prompt, because on an unattended machine a device code printed to a log
    /// nobody reads looks exactly like a hang.
    /// </summary>
    private static async Task<int> RunLoginAsync()
    {
        DeviceCodeAuthOptions options;
        try
        {
            options = DeviceCodeAuthEnvironment.FromEnvironment();
        }
        catch (AgentConfigException ex)
        {
            AgentLog.Error(ex.Message);
            return 1;
        }

        using var shutdown = RegisterShutdownHandlers();
        var provider = new DeviceCodeAccessTokenProvider(options, allowInteractive: true);

        try
        {
            // The token is acquired only to prove sign-in worked and to populate
            // the cache; it is deliberately not echoed, stored or returned.
            _ = await provider.GetAccessTokenAsync(shutdown.Token);
            AgentLog.Info("Signed in. The credential is cached for this user on this machine; the service can now start.");
            return 0;
        }
        catch (OperationCanceledException)
        {
            AgentLog.Warn("Sign-in cancelled; nothing was cached.");
            return 1;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Sign-in failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Chooses how the agent authenticates. Setting the client id opts into the
    /// device-code flow; otherwise the bearer token comes from the environment.
    /// The daemon never signs in interactively - it can only reuse what
    /// <c>--login</c> cached, and says so if that is missing.
    /// </summary>
    internal static IAccessTokenProvider CreateAccessTokenProvider()
    {
        var clientId = Environment.GetEnvironmentVariable(DeviceCodeAuthEnvironment.ClientIdVariable);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return new EnvironmentAccessTokenProvider();
        }

        return new DeviceCodeAccessTokenProvider(
            DeviceCodeAuthEnvironment.FromEnvironment(),
            allowInteractive: false);
    }

    /// <summary>The daemon and --once paths: full configuration, real publisher.</summary>
    private static async Task<int> RunConfiguredAsync(AgentLaunchMode mode)
    {
        // When device-code authentication is configured the bearer token comes
        // from the cached credential, so VIBEMETER_AGENT_TOKEN is not required -
        // demanding it would make a correctly configured machine refuse to start.
        var usingDeviceCode = !string.IsNullOrWhiteSpace(
            Environment.GetEnvironmentVariable(DeviceCodeAuthEnvironment.ClientIdVariable));

        AgentConfig config;
        try
        {
            config = AgentConfig.FromEnvironment(
                requireApiBaseUrl: true,
                requireToken: !usingDeviceCode);
        }
        catch (AgentConfigException ex)
        {
            AgentLog.Error(ex.Message);
            return 1;
        }

        using var shutdown = RegisterShutdownHandlers();

        var queue = new OfflineQueue(config.QueueDirectory);
        // Non-null: FromEnvironment() requires a base URL - only --dry-run runs without one.
        IAccessTokenProvider tokenProvider;
        try
        {
            tokenProvider = CreateAccessTokenProvider();
        }
        catch (AgentConfigException ex)
        {
            AgentLog.Error(ex.Message);
            return 1;
        }

        using var publisher = new SnapshotPublisher(config.ApiBaseUrl!, tokenProvider);
        var host = new AgentHost(config, CreateProviders(), new SnapshotMapper(), publisher, queue);

        if (mode == AgentLaunchMode.Once)
        {
            try
            {
                var outcome = await host.RunOneCycleAsync(shutdown.Token);
                return OnceExitCode(outcome);
            }
            catch (OperationCanceledException)
            {
                AgentLog.Warn("Cancelled before the cycle completed; nothing was published.");
                return 1;
            }
            catch (Exception ex)
            {
                AgentLog.Error($"Fatal: {ex.Message}");
                return 1;
            }
        }

        var exitCode = 0;
        try
        {
            await host.RunAsync(shutdown.Token);
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Fatal: {ex.Message}");
            exitCode = 1;
        }

        // One last bounded drain so a Ctrl+C mid-outage loses nothing recoverable.
        try
        {
            using var finalFlush = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await host.FlushQueueAsync(finalFlush.Token);
        }
        catch (Exception)
        {
            // Shutdown flush is best-effort; anything left stays queued on disk.
        }

        return exitCode;
    }

    private static async Task<int> RunDryRunAsync()
    {
        AgentConfig config;
        try
        {
            // A dry-run verifies COLLECTION on this machine: no token is read
            // (real authentication does not exist yet) and no API URL is needed
            // because nothing is ever sent. Everything that IS set - interval,
            // staleness threshold, a base URL - is still validated and honoured.
            config = AgentConfig.FromEnvironment(requireApiBaseUrl: false, requireToken: false);
        }
        catch (AgentConfigException ex)
        {
            AgentLog.Error(ex.Message);
            return 1;
        }

        using var shutdown = RegisterShutdownHandlers();

        // The publisher seat is filled by a guard that throws if ever reached;
        // DryRunRunner never calls it. (Tests fill the same seat with a
        // recording publisher to pin the publishes-nothing guarantee.)
        var runner = new DryRunRunner(
            config,
            CreateProviders(),
            new SnapshotMapper(),
            new NeverPublishPublisher(),
            Console.Out);

        try
        {
            return await runner.RunAsync(shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            AgentLog.Warn("Dry-run cancelled before it completed.");
            return 1;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Fatal: {ex.Message}");
            return 1;
        }
    }

    /// <summary>--once exit semantics: 0 = published, nothing to publish, or
    /// deliberately not worth publishing; non-zero = the cycle failed or the
    /// snapshot was only queued. A policy skip is a success: the run did its
    /// job and concluded the server already knows, so a scheduled --once that
    /// runs more often than the policy publishes must not report failure.</summary>
    internal static int OnceExitCode(CycleOutcome outcome) =>
        outcome is CycleOutcome.Published or CycleOutcome.NothingToPublish or CycleOutcome.SkippedByPolicy ? 0 : 1;

    // Same provider set the WPF app registers; each constructs parameterless
    // and reports NotConfigured itself when its local credentials are absent.
    private static IUsageProvider[] CreateProviders() =>
    [
        new ClaudeProvider(),
        new CodexProvider(),
        new ZaiProvider(),
        new GoogleProvider(),
    ];

    private static CancellationTokenSource RegisterShutdownHandlers()
    {
        var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true; // let Main shut down cleanly instead of dying mid-write
            shutdown.Cancel();
        };

        // Deliberately not disposed - the registration must live for the whole process.
        RegisterSigTerm(shutdown);
        return shutdown;
    }

    private static PosixSignalRegistration? RegisterSigTerm(CancellationTokenSource shutdown)
    {
        try
        {
            return PosixSignalRegistration.Create(PosixSignal.SIGTERM, _ => shutdown.Cancel());
        }
        catch (Exception ex)
        {
            AgentLog.Warn($"SIGTERM handler unavailable ({ex.Message}); falling back to Ctrl+C only.");
            return null;
        }
    }
}
