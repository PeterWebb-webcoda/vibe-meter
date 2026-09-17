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
public sealed record AgentConfig(Uri ApiBaseUrl, TimeSpan Interval, string QueueDirectory)
{
    public const string ApiBaseUrlVariable = "VIBEMETER_API_BASE_URL";
    public const string IntervalSecondsVariable = "VIBEMETER_AGENT_INTERVAL_SECONDS";
    public const string TokenVariable = "VIBEMETER_AGENT_TOKEN";
    public const int MinimumIntervalSeconds = 10;
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Reads and validates configuration, aggregating every problem into one
    /// exception. The token is checked for presence only — it is never copied
    /// into the config or logged; <see cref="AccessToken.EnvironmentAccessTokenProvider"/>
    /// reads it fresh for every publish.
    /// </summary>
    public static AgentConfig FromEnvironment()
    {
        var problems = new List<string>();

        Uri? apiBaseUrl = null;
        var rawBaseUrl = Environment.GetEnvironmentVariable(ApiBaseUrlVariable);
        if (string.IsNullOrWhiteSpace(rawBaseUrl))
        {
            problems.Add($"{ApiBaseUrlVariable} is required - set it to the collection API root, e.g. {ApiBaseUrlVariable}=https://api.example.com");
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
        if (string.IsNullOrWhiteSpace(token))
        {
            problems.Add($"{TokenVariable} is required - set it to the agent's bearer token before starting.");
        }

        if (problems.Count > 0)
        {
            throw new AgentConfigException(
                "The VibeMeter agent is not configured correctly:"
                + Environment.NewLine + "  - " + string.Join(Environment.NewLine + "  - ", problems));
        }

        return new AgentConfig(apiBaseUrl!, interval, DefaultQueueDirectory());
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
