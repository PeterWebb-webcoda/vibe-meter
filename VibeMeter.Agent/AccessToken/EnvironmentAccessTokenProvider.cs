namespace VibeMeter.Agent.AccessToken;

/// <summary>
/// Reads the bearer token from the <see cref="VariableName"/> environment
/// variable on every call — the token is never cached or copied, so rotation
/// takes effect on the next publish without a restart. This is the only
/// implementation provided; a human owns real authentication (MSAL /
/// device-code) and will add it later against <see cref="IAccessTokenProvider"/>.
/// </summary>
public sealed class EnvironmentAccessTokenProvider : IAccessTokenProvider
{
    public const string VariableName = "VIBEMETER_AGENT_TOKEN";

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var token = Environment.GetEnvironmentVariable(VariableName);
        if (string.IsNullOrWhiteSpace(token))
        {
            var hint = OperatingSystem.IsWindows()
                ? $"setx {VariableName} \"<token>\""
                : $"export {VariableName}=\"<token>\"";
            throw new InvalidOperationException(
                $"Environment variable '{VariableName}' is not set. Provide the agent's bearer token "
                + $"(e.g. {hint}), or install an alternative IAccessTokenProvider.");
        }

        return Task.FromResult(token);
    }
}
