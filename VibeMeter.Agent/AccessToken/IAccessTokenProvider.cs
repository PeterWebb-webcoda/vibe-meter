namespace VibeMeter.Agent.AccessToken;

/// <summary>
/// Supplies the bearer token the agent presents to the collection API.
/// Deliberately tiny so a second implementation (MSAL / OAuth device-code) can
/// be added later without touching the publisher, the queue, or the host.
/// </summary>
public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}
