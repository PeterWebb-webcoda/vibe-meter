using System;
using System.Collections.Generic;
using System.Linq;
using VibeMeter.Core;
using VibeMeter.Providers.Claude;
using VibeMeter.Providers.Codex;
using VibeMeter.Providers.Google;
using VibeMeter.Providers.Zai;

namespace VibeMeter.Services;

/// <summary>
/// The single place where every <see cref="IUsageProvider"/> is registered.
/// Add a new provider by constructing it here; the rest of the app picks it up.
/// </summary>
public class ProviderRegistry
{
    public IReadOnlyList<IUsageProvider> Providers { get; }

    public ProviderRegistry()
    {
        Providers = new List<IUsageProvider>
        {
            new CodexProvider(),
            new ClaudeProvider(),
            new ZaiProvider(),
            // Google (AI Pro / Antigravity) is parked for now: there is no public
            // usage API, and the only route (scraping gemini.google.com's private
            // batchexecute RPC) is blocked by Chrome 127+ app-bound cookie encryption.
            // The GoogleProvider/GoogleAuth files remain for when we revisit it.
            // See docs/provider-research.md for the full investigation.
            // new GoogleProvider()
        };
    }

    public IUsageProvider? Get(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
