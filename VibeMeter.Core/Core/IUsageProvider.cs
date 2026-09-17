using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VibeMeter.Core;

/// <summary>
/// The contract every AI usage provider implements. Each provider knows how to
/// authenticate and fetch its own usage/rate-limit data, returning a normalised
/// <see cref="ProviderUsage"/> envelope that the UI binds to regardless of source.
/// </summary>
public interface IUsageProvider
{
    /// <summary>Stable lowercase identifier, e.g. "codex", "claude", "zai", "google".</summary>
    string Id { get; }

    /// <summary>Human-friendly name shown in the UI, e.g. "Codex".</summary>
    string DisplayName { get; }

    /// <summary>
    /// Fetches the latest usage snapshot. Should never throw for expected conditions
    /// (missing credentials, network errors); instead return a <see cref="ProviderUsage"/>
    /// with <see cref="ProviderState.NotConfigured"/> or <see cref="ProviderState.Error"/>.
    /// </summary>
    Task<ProviderUsage> FetchAsync();
}
