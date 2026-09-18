using VibeMeter.Core;

namespace VibeMeter.Agent;

/// <summary>
/// The collection half of an agent cycle: fetch every registered provider in
/// parallel and hand the reports on. Deliberately the agent's own — a headless
/// daemon polls the whole registry on a timer, whereas the tray app fetches
/// only the providers the user enabled and updates each card as its own fetch
/// lands, so there is no collection behaviour worth sharing between them. What
/// both hosts do share begins one step later, at
/// <see cref="VibeMeter.Publishing.SnapshotPublishCycle"/>.
/// </summary>
internal sealed class ProviderCollector
{
    // A provider that neither returns nor throws within this window is reported
    // as an error for the cycle, keeping the interval honest. (IUsageProvider.
    // FetchAsync predates the agent and takes no CancellationToken.)
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(60);

    private readonly IReadOnlyList<IUsageProvider> _providers;

    public ProviderCollector(IReadOnlyList<IUsageProvider> providers)
    {
        _providers = providers;
    }

    /// <summary>
    /// One collection pass, in registry order. A null result means the cycle
    /// could not collect at all; the reason is already logged. A single
    /// provider failing is not that: it reports an error result and the cycle
    /// carries on with the rest.
    /// </summary>
    internal async Task<IReadOnlyList<ProviderUsage>?> CollectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await FetchAllAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            AgentLog.Error($"Provider collection failed: {ex.Message}");
            return null;
        }
    }

    private async Task<List<ProviderUsage>> FetchAllAsync(CancellationToken cancellationToken)
    {
        var fetches = _providers.Select(async provider =>
        {
            try
            {
                // WaitAsync both bounds a hung provider and lets shutdown
                // interrupt an in-flight fetch despite FetchAsync taking no token.
                return await provider.FetchAsync().WaitAsync(FetchTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                AgentLog.Error($"Provider '{provider.Id}' did not respond within {FetchTimeout.TotalSeconds:0}s.");
                return ErrorUsage(provider);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Providers promise not to throw for expected conditions; this is
                // the belt-and-braces path so one rogue provider can never end the process.
                AgentLog.Error($"Provider '{provider.Id}' fetch failed: {ex.Message}");
                return ErrorUsage(provider);
            }
        });

        return [.. await Task.WhenAll(fetches)];
    }

    private static ProviderUsage ErrorUsage(IUsageProvider provider) => new()
    {
        ProviderId = provider.Id,
        DisplayName = provider.DisplayName,
        State = ProviderState.Error,
    };
}
