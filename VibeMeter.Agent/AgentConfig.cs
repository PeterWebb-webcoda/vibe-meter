namespace VibeMeter.Agent;

/// <summary>
/// Thrown when the environment fails configuration validation. The message is
/// written to stderr verbatim and the process exits non-zero.
/// </summary>
public sealed class AgentConfigException(string message) : Exception(message);

/// <summary>
/// Agent configuration, read once from environment variables and validated up
/// front so a misconfigured agent fails fast with a clear message instead of
/// limping through a run.
/// </summary>
/// <param name="ApiBaseUrl">
/// The collection API root. <see langword="null"/> only when the caller said a
/// base URL is not required — the --dry-run path, which sends nothing, so it
/// has nothing to send it to.
/// </param>
/// <param name="StalenessThreshold">
/// How old a provider's UNDERLYING data may be before the agent omits it from
/// the snapshot — not how long ago the agent polled. File-derived providers
/// (Claude reads <c>~/.claude/usage_cache.json</c> and the desktop app's
/// history) only refresh while that surface runs on this machine, so an idle
/// machine would otherwise stamp days-old figures with a fresh publish time.
/// The collection API merges per provider newest-first by publish time, so a
/// stale-but-published reading masks a fresh one from a machine in use; the
/// agent therefore omits anything older than this threshold (never a
/// state="stale" downgrade — the merge ignores state). The default of 20
/// minutes rides out a few publish intervals (default interval: 5 minutes) of
/// ordinary idleness while staying far inside Claude's shortest (5-hour)
/// window. See <c>docs/agent.md</c>.
/// </param>
public sealed record AgentConfig(Uri? ApiBaseUrl, TimeSpan Interval, string QueueDirectory, TimeSpan StalenessThreshold)
{
    public const string ApiBaseUrlVariable = "VIBEMETER_API_BASE_URL";
    public const string IntervalSecondsVariable = "VIBEMETER_AGENT_INTERVAL_SECONDS";
    public const string TokenVariable = "VIBEMETER_AGENT_TOKEN";
    public const string StalenessMinutesVariable = "VIBEMETER_AGENT_STALENESS_MINUTES";
    public const int MinimumIntervalSeconds = 10;
    public const int MinimumStalenessMinutes = 1;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan DefaultStalenessThreshold = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Reads and validates configuration with the full requirements of the
    /// daemon and <c>--once</c> modes: an API base URL and a bearer token must
    /// both be present.
    /// </summary>
    public static AgentConfig FromEnvironment() => FromEnvironment(requireApiBaseUrl: true, requireToken: true);

    /// <summary>
    /// Reads and validates configuration, aggregating every problem into one
    /// exception. The token is checked for presence only — it is never copied
    /// into the config or logged; <see cref="AccessToken.EnvironmentAccessTokenProvider"/>
    /// reads it fresh for every publish.
    /// </summary>
    /// <param name="requireApiBaseUrl">
    /// Whether <see cref="ApiBaseUrlVariable"/> must be set. Only --dry-run may
    /// run without it: it sends nothing, so it has nothing to send it to.
    /// Values that ARE set are still validated.
    /// </param>
    /// <param name="requireToken">
    /// Whether <see cref="TokenVariable"/> must be set. Only --dry-run may run
    /// without it — there the token is genuinely absent (real authentication
    /// is not implemented yet), never replaced by a dummy value, and the dry
    /// run never reads it at all because it never publishes.
    /// </param>
    public static AgentConfig FromEnvironment(bool requireApiBaseUrl, bool requireToken)
    {
        var problems = new List<string>();

        Uri? apiBaseUrl = null;
        var rawBaseUrl = Environment.GetEnvironmentVariable(ApiBaseUrlVariable);
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            if (requireApiBaseUrl)
            {
                problems.Add($"{ApiBaseUrlVariable} is required - set it to the collection API root, e.g. {ApiBaseUrlVariable}=https://api.example.com");
            }
        }
        else if (!Uri.TryCreate(rawBaseUrl.Trim().TrimEnd('/'), UriKind.Absolute, out apiBaseUrl)
                 || (apiBaseUrl!.Scheme != Uri.UriSchemeHttp && apiBaseUrl.Scheme != Uri.UriSchemeHttps))
        {
            problems.Add($"{ApiBaseUrlVariable} must be an absolute http(s) URL (got '{rawBaseUrl.Trim()}').");
        }

        var interval = DefaultInterval;
        var rawInterval = Environment.GetEnvironmentVariable(IntervalSecondsVariable);
        if (!string.IsNullOrWhiteSpace(rawInterval))
        {
            if (!int.TryParse(rawInterval.Trim(), out var seconds))
            {
                problems.Add($"{IntervalSecondsVariable} must be a whole number of seconds (got '{rawInterval.Trim()}').");
            }
            else if (seconds < MinimumIntervalSeconds)
            {
                problems.Add($"{IntervalSecondsVariable} must be at least {MinimumIntervalSeconds} seconds.");
            }
            else
            {
                interval = TimeSpan.FromSeconds(seconds);
            }
        }

        var token = Environment.GetEnvironmentVariable(TokenVariable);
        if (requireToken && string.IsNullOrWhiteSpace(token))
        {
            problems.Add($"{TokenVariable} is required - set it to the agent's bearer token before starting.");
        }

        var stalenessThreshold = DefaultStalenessThreshold;
        var rawStaleness = Environment.GetEnvironmentVariable(StalenessMinutesVariable);
        if (!string.IsNullOrWhiteSpace(rawStaleness))
        {
            if (!int.TryParse(rawStaleness.Trim(), out var stalenessMinutes))
            {
                problems.Add($"{StalenessMinutesVariable} must be a whole number of minutes (got '{rawStaleness.Trim()}').");
            }
            else if (stalenessMinutes < MinimumStalenessMinutes)
            {
                problems.Add($"{StalenessMinutesVariable} must be at least {MinimumStalenessMinutes} minute(s).");
            }
            else
            {
                stalenessThreshold = TimeSpan.FromMinutes(stalenessMinutes);
            }
        }

        if (problems.Count > 0)
        {
            throw new AgentConfigException(
                "The VibeMeter agent is not configured correctly:"
                + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems));
        }

        return new AgentConfig(apiBaseUrl!, interval, DefaultQueueDirectory(), stalenessThreshold);
    }

    private static string DefaultQueueDirectory() =>
        // SpecialFolder.ApplicationData maps to %APPDATA% on Windows (roaming)
        // and to $XDG_DATA_HOME or ~/.local/share on Linux - both durable and
        // user-specific on their respective platforms.
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VibeMeter",
            "VibeMeter.Agent",
            "offline-queue");
}
