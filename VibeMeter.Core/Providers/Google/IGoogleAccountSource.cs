using System;
using System.Collections.Generic;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Where a host's explicitly-configured Google accounts come from. Asked on
/// every fetch, so the answer is always the host's current one.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a process-wide <c>static</c> list that only the WPF view
/// model's constructor ever assigned. A static made the roster a property of
/// the PROCESS rather than of the host that built the provider: any host that
/// did not assign it — the agent, a dry run, anything constructing
/// <see cref="GoogleProvider"/> outside the view model — saw no configured
/// accounts at all, and reported "not configured" for an account that was
/// sitting in the settings file. Since the same reading is what the tray app
/// publishes, that answer then went to the server and masked a good reading
/// from a host that had read the account correctly.
/// </para>
/// <para>
/// An interface asked per fetch also removes the ordering hazard: there is no
/// window between constructing a provider and populating it, and no way for a
/// second host in the same process to overwrite the first's accounts.
/// </para>
/// </remarks>
public interface IGoogleAccountSource
{
    /// <summary>
    /// The accounts added through VibeMeter's own OAuth flow, as they stand now.
    /// Accounts with no usable refresh token in this session are the provider's
    /// to filter, not this method's to hide.
    /// </summary>
    IReadOnlyList<GoogleAccount> GetConfiguredAccounts();
}

/// <summary>
/// The source for a host that configures no accounts of its own — the headless
/// agent, which relies entirely on the auto-detected Antigravity account.
/// </summary>
public sealed class EmptyGoogleAccountSource : IGoogleAccountSource
{
    public static readonly EmptyGoogleAccountSource Instance = new();

    public IReadOnlyList<GoogleAccount> GetConfiguredAccounts() => Array.Empty<GoogleAccount>();
}

/// <summary>
/// A source over a list the caller owns and may replace. Useful to a host that
/// already holds its accounts in memory, and to tests.
/// </summary>
public sealed class FixedGoogleAccountSource : IGoogleAccountSource
{
    private readonly Func<IReadOnlyList<GoogleAccount>> _read;

    public FixedGoogleAccountSource(IReadOnlyList<GoogleAccount> accounts) => _read = () => accounts;

    /// <param name="read">Evaluated on every fetch, so a host can hand over a live view.</param>
    public FixedGoogleAccountSource(Func<IReadOnlyList<GoogleAccount>> read) => _read = read;

    public IReadOnlyList<GoogleAccount> GetConfiguredAccounts() =>
        _read() ?? Array.Empty<GoogleAccount>();
}
