using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace VibeMeter.Agent.AccessToken;

/// <summary>
/// Acquires the collection API token from Microsoft Entra using the OAuth 2.0
/// device authorisation flow, which needs no browser or redirect listener on the
/// machine running the agent.
/// </summary>
/// <remarks>
/// <para>
/// Interactive sign-in is opt-in. The daemon constructs this with
/// <c>allowInteractive: false</c> so it can only ever refresh a token that an
/// operator has already established; if that is not possible it fails with an
/// instruction rather than printing a code nobody is watching and blocking
/// forever on an unattended machine.
/// </para>
/// <para>
/// The token cache is MSAL's encrypted store on Windows and macOS, but an
/// unprotected file on Linux. That is deliberate: the default Linux cache needs
/// libsecret and a running keyring, neither of which a headless server has, and
/// the failure is an obscure one at first refresh. The file is created under the
/// per-user application data directory; on a shared host it should be treated as
/// a credential and its permissions restricted accordingly.
/// </para>
/// </remarks>
public sealed class DeviceCodeAccessTokenProvider : IAccessTokenProvider
{
    private readonly DeviceCodeAuthOptions _options;
    private readonly bool _allowInteractive;
    private readonly Action<string> _notify;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _application;

    public DeviceCodeAccessTokenProvider(
        DeviceCodeAuthOptions options,
        bool allowInteractive,
        Action<string>? notify = null)
    {
        _options = options;
        _allowInteractive = allowInteractive;
        _notify = notify ?? Console.WriteLine;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var application = await GetApplicationAsync().ConfigureAwait(false);

        var accounts = await application.GetAccountsAsync().ConfigureAwait(false);
        var account = accounts.FirstOrDefault();

        if (account is not null)
        {
            try
            {
                var silent = await application
                    .AcquireTokenSilent(_options.Scopes, account)
                    .ExecuteAsync(cancellationToken)
                    .ConfigureAwait(false);

                return silent.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                // Falls through: the cached refresh token is gone or no longer
                // satisfies policy (a password change or a sign-in frequency
                // condition will do this), so a human has to sign in again.
            }
        }

        if (!_allowInteractive)
        {
            throw new InvalidOperationException(
                "No usable cached credential, and interactive sign-in is disabled for the daemon. "
                + "Run the agent once with --login on this machine to sign in, then start the service again.");
        }

        var result = await application
            .AcquireTokenWithDeviceCode(
                _options.Scopes,
                deviceCode =>
                {
                    // deviceCode.Message carries the verification URL and user code.
                    // It contains no token and is safe to display.
                    _notify(deviceCode.Message);
                    return Task.CompletedTask;
                })
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return result.AccessToken;
    }

    private async Task<IPublicClientApplication> GetApplicationAsync()
    {
        if (_application is not null)
        {
            return _application;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_application is not null)
            {
                return _application;
            }

            var application = PublicClientApplicationBuilder
                .Create(_options.ClientId)
                .WithAuthority(_options.Authority)
                .Build();

            Directory.CreateDirectory(_options.CacheDirectory);

            var storage = new StorageCreationPropertiesBuilder(
                    DeviceCodeAuthOptions.CacheFileName,
                    _options.CacheDirectory)
                .WithLinuxUnprotectedFile()
                .WithMacKeyChain("VibeMeter.Agent", "MsalCache")
                .Build();

            var cache = await MsalCacheHelper.CreateAsync(storage).ConfigureAwait(false);
            cache.RegisterCache(application.UserTokenCache);

            _application = application;
            return application;
        }
        finally
        {
            _gate.Release();
        }
    }
}
