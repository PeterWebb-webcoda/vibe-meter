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
            // Google AI Pro / Antigravity: per-model quota via Google's Cloud Code
            // backend (cloudcode-pa.googleapis.com), authed with the OAuth refresh token
            // the Gemini CLI / Antigravity store in ~/.gemini/oauth_creds.json. See
            // docs/provider-research.md for the full API investigation.
            new GoogleProvider()
        };
    }

    public IUsageProvider? Get(string id) =>
        Providers.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
