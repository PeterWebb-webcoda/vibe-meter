namespace VibeMeter.Agent.AccessToken;

/// <summary>
/// Identity configuration for <see cref="DeviceCodeAccessTokenProvider"/>.
/// </summary>
/// <remarks>
/// Every value is required from the environment and none is defaulted in source.
/// The client, tenant and scope identifiers are not secrets, but baking a
/// particular organisation's directory into this repository would tie a general
/// tool to one tenant; keeping them in configuration leaves it portable.
/// </remarks>
public sealed record DeviceCodeAuthOptions(string ClientId, string TenantId, string Scope, string CacheDirectory)
{
    public const string ClientIdVariable = "VIBEMETER_AGENT_CLIENT_ID";
    public const string TenantIdVariable = "VIBEMETER_AGENT_TENANT_ID";
    public const string ScopeVariable = "VIBEMETER_AGENT_SCOPE";

    /// <summary>The MSAL token cache file name. The directory is per-user.</summary>
    public const string CacheFileName = "agent-token-cache.bin";

    public string Authority => $"https://login.microsoftonline.com/{TenantId}";

    public string[] Scopes => [Scope];

    /// <summary>
    /// Reads the options from the environment, reporting every missing variable
    /// at once rather than one per run — a headless operator should not have to
    /// restart four times to discover four problems.
    /// </summary>
    public static DeviceCodeAuthOptions FromEnvironment()
    {
        var problems = new List<string>();

        var clientId = Read(ClientIdVariable, "the Entra application (client) id of the agent's public-client registration", problems);
        var tenantId = Read(TenantIdVariable, "the Entra directory (tenant) id", problems);
        var scope = Read(ScopeVariable, "the delegated scope to request, e.g. api://<api-app-id>/Usage.Write", problems);

        if (problems.Count > 0)
        {
            throw new AgentConfigException(
                "Device-code authentication is not configured:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem)));
        }

        var cacheDirectory = Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.ApplicationData,
                Environment.SpecialFolderOption.Create),
            "VibeMeter");

        return new DeviceCodeAuthOptions(clientId!, tenantId!, scope!, cacheDirectory);
    }

    private static string? Read(string variable, string description, List<string> problems)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{variable} is required - set it to {description}.");
            return null;
        }

        return value.Trim();
    }
}
