namespace VibeMeter.Agent;

/// <summary>How the agent was asked to run.</summary>
public enum AgentLaunchMode
{
    /// <summary>No arguments: the unattended daemon loop.</summary>
    Daemon,

    /// <summary>--once: a single collect-map-publish cycle, then exit.</summary>
    Once,

    /// <summary>--dry-run (alone or alongside --once): collect and print, never publish.</summary>
    DryRun,

    /// <summary>--help: show the modes and environment variables.</summary>
    Help,

    /// <summary>Unrecognised arguments: report and exit non-zero.</summary>
    UsageError,
}

/// <summary>
/// Parses the agent's command line. Deliberately tiny: three flags, no values.
/// Unknown arguments are a hard usage error — the process must never fall
/// through to the daemon loop because of a typo.
/// </summary>
/// <param name="Mode">The mode the arguments select.</param>
/// <param name="UsageError">For <see cref="AgentLaunchMode.UsageError"/>, the message to show.</param>
public sealed record CliOptions(AgentLaunchMode Mode, string? UsageError = null)
{
    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var once = false;
        var dryRun = false;
        var help = false;
        var unknown = new List<string>();

        foreach (var arg in args)
        {
            switch (arg)
            {
                case "--once":
                    once = true;
                    break;

                case "--dry-run":
                    dryRun = true;
                    break;

                case "--help" or "-h":
                    help = true;
                    break;

                default:
                    unknown.Add(arg);
                    break;
            }
        }

        if (unknown.Count > 0)
        {
            return new CliOptions(
                AgentLaunchMode.UsageError,
                $"Unrecognised argument(s): {string.Join(", ", unknown)}. Only --once, --dry-run and --help are accepted.");
        }

        // --help wins over everything; --once --dry-run behaves as a dry-run.
        if (help)
        {
            return new CliOptions(AgentLaunchMode.Help);
        }

        if (dryRun)
        {
            return new CliOptions(AgentLaunchMode.DryRun);
        }

        if (once)
        {
            return new CliOptions(AgentLaunchMode.Once);
        }

        return new CliOptions(AgentLaunchMode.Daemon);
    }

    public const string HelpText = """
        VibeMeter.Agent - headless AI-usage collector for the VibeMeter collection API.

        Usage: VibeMeter.Agent [--once | --dry-run | --help]

        Modes:
          (no arguments)  Daemon: collect from every provider and publish a snapshot
                          on a timer until stopped with Ctrl+C or SIGTERM.
          --once          Run exactly one collect-map-publish cycle and exit.
                          Exit 0 when the snapshot was published or there was
                          nothing to publish; non-zero when the cycle failed or
                          the snapshot could only be queued for a later attempt.
          --dry-run       Collect, apply the freshness rules and print the snapshot
                          that WOULD be published. Publishes nothing to the
                          collection API and needs no token - made for verifying a
                          new machine before authentication exists. Exit 0 when at
                          least one provider is publishable; non-zero when none is.
          --help          Show this help.

          --once --dry-run behaves as --dry-run. Unrecognised arguments are an error.

        Environment variables:
          VIBEMETER_API_BASE_URL             Collection API root, e.g.
                                             https://api.example.com. Required for
                                             the daemon and --once; optional for
                                             --dry-run (nothing is sent).
          VIBEMETER_AGENT_TOKEN              Bearer token for the collection API.
                                             Required for the daemon and --once;
                                             never read by --dry-run.
          VIBEMETER_AGENT_INTERVAL_SECONDS   Daemon publish interval in seconds
                                             (default 300, minimum 10).
          VIBEMETER_AGENT_STALENESS_MINUTES  How old a provider's underlying data
                                             may be before it is omitted, in
                                             minutes (default 20, minimum 1).

        Exit codes: 0 success (see the mode descriptions) · 1 runtime failure (bad
        configuration, failed cycle, nothing publishable) · 2 usage error.
        """;
}
