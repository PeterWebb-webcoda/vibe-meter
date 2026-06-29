using System.Threading.Tasks;
using VibeMeter.Core;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Google AI Pro / Antigravity (Gemini) provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Research outcome:</b> Google does not expose a public usage-meter REST API for the
/// AI Pro / AI Ultra subscriptions or for Antigravity. Usage is enforced server-side via a
/// token-burn model over rolling 5-hour and weekly windows and surfaced only as in-app
/// notifications inside Antigravity itself. There is no local quota file either — the
/// <c>~/.gemini/</c> tree holds conversations (opaque protobuf), onboarding state, and the
/// signed-in account, but no remaining-quota figures.
/// </para>
/// <para>
/// This provider therefore detects the local Antigravity install and signed-in account,
/// and reports an accurate, app-only state rather than fabricating figures. (The Google
/// Cloud Console quota page covers Vertex AI / AI Studio API projects, which is a
/// separate, pay-per-use surface from the AI Pro subscription.)
/// </para>
/// </remarks>
public sealed class GoogleProvider : IUsageProvider
{
    public string Id => "google";
    public string DisplayName => "Google AI Pro";

    private readonly GoogleAuth _auth;

    /// <summary>Production constructor.</summary>
    public GoogleProvider() : this(new GoogleAuth()) { }

    /// <summary>Testable constructor.</summary>
    public GoogleProvider(GoogleAuth auth) => _auth = auth;

    public async Task<ProviderUsage> FetchAsync()
    {
        string? account = null;
        try
        {
            account = await _auth.GetAccountEmailAsync();
        }
        catch
        {
            // Non-fatal: fall through to the not-configured handling below.
        }

        if (_auth.IsConfigured)
        {
            return new ProviderUsage
            {
                ProviderId = Id,
                DisplayName = DisplayName,
                State = ProviderState.NotConfigured,
                PlanLabel = account,
                ErrorMessage =
                    "Antigravity is installed" +
                    (account is null ? "" : $" (signed in as {account})") +
                    ", but Google exposes no public usage meter for AI Pro. " +
                    "Quotas (5h / weekly) are shown only inside Antigravity."
            };
        }

        return new ProviderUsage
        {
            ProviderId = Id,
            DisplayName = DisplayName,
            State = ProviderState.NotConfigured,
            ErrorMessage =
                "Install Antigravity and sign in with a Google AI Pro / Ultra account " +
                "to enable Google. Note: Google has no public usage API — quotas are " +
                "visible only inside Antigravity."
        };
    }
}
