using System.Runtime.InteropServices;
using VibeMeter.Agent;
using VibeMeter.Agent.AccessToken;
using VibeMeter.Agent.Publishing;
using VibeMeter.Core;
using VibeMeter.Providers.Claude;
using VibeMeter.Providers.Codex;
using VibeMeter.Providers.Google;
using VibeMeter.Providers.Zai;

namespace VibeMeter.Agent;

/// <summary>
/// Headless VibeMeter agent: collects usage from every provider, maps it onto
/// the collection API's snapshot contract, and POSTs it on a timer — queueing
/// snapshots for later whenever the API is unreachable.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        AgentConfig config;
        try
        {
            config = AgentConfig.FromEnvironment();
        }
        catch (AgentConfigException ex)
        {
            AgentLog.Error(ex.Message);
            return 1;
        }

        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true; // let Main shut down cleanly instead of dying mid-write
            shutdown.Cancel();
        };
        using PosixSignalRegistration? sigterm = RegisterSigTerm(shutdown);

        // Same provider set the WPF app registers; each constructs parameterless
        // and reports NotConfigured itself when its local credentials are absent.
        IUsageProvider[] providers =
        [
            new ClaudeProvider(),
            new CodexProvider(),
            new ZaiProvider(),
            new GoogleProvider(),
        ];

        var queue = new OfflineQueue(config.QueueDirectory);
        using var publisher = new SnapshotPublisher(config.ApiBaseUrl, new EnvironmentAccessTokenProvider());
        var host = new AgentHost(config, providers, new SnapshotMapper(), publisher, queue);

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
