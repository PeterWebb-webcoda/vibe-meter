namespace VibeMeter.Publishing.AccessToken;

/// <summary>
/// Identity configuration for <see cref="DeviceCodeAccessTokenProvider"/>.
/// </summary>
/// <remarks>
/// Every value is supplied by the host and none is defaulted in source. The
/// client, tenant and scope identifiers are not secrets, but baking a
/// particular organisation's directory into this repository would tie a general
/// tool to one tenant; keeping them in the host's configuration leaves it
/// portable. The headless agent reads them from the environment
/// (<c>DeviceCodeAuthEnvironment</c>); a desktop host is free to read them from
/// wherever it keeps its own settings.
/// </remarks>
public sealed record DeviceCodeAuthOptions(string ClientId, string TenantId, string Scope, string CacheDirectory)
{
    /// <summary>The MSAL token cache file name. The directory is per-user.</summary>
    public const string CacheFileName = "agent-token-cache.bin";

    public string Authority => $"https://login.microsoftonline.com/{TenantId}";

    public string[] Scopes => [Scope];
}
