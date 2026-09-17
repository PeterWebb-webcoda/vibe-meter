using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace VibeMeter.Providers.Google;

/// <summary>
/// Runs a Google OAuth2 PKCE authorisation-code flow to mint a VibeMeter-owned refresh
/// token. Spins up a loopback HTTP listener, opens the user's browser to Google's consent
/// page, awaits the callback, and exchanges the code for tokens.
/// </summary>
/// <remarks>
/// <para>
/// Reuses the Antigravity Cockpit's public OAuth client (see <see cref="GoogleAuth.ClientId"/>)
/// so the resulting refresh token refreshes with the same credentials. Google will label
/// the consent screen "Antigravity Cockpit" — functionally fine, zero client setup.
/// </para>
/// <para>
/// PKCE (RFC 7636) protects the authorisation-code exchange on native apps that cannot
/// keep a client secret truly confidential. We generate a random <c>code_verifier</c>,
/// send its S256 hash as the <c>code_challenge</c>, and present the verifier at token
/// exchange. Google's loopback-redirect rule (RFC 8252 §8.3) means we listen on
/// <c>http://localhost:&lt;port&gt;/callback/</c> and use that as the registered
/// <c>redirect_uri</c>.
/// </para>
/// </remarks>
public static class GoogleOAuthFlow
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string RedirectHost = "localhost";
    private const int RedirectPort = 8484;
    private const string RedirectPath = "/callback/";

    /// <summary>
    /// Runs the full interactive flow. Opens the browser, waits for the user to consent,
    /// returns the new account's email + refresh token. Throws on timeout, listener
    /// failure, or Google erroring the exchange.
    /// </summary>
    /// <param name="timeout">How long to wait for the user to complete consent.</param>
    public static async Task<(string Email, string RefreshToken)> RunAsync(
        TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromMinutes(5));

        // 1. PKCE: generate verifier + S256 challenge.
        var verifier = GenerateCodeVerifier();
        var challenge = ComputeS256Challenge(verifier);

        var redirectUri = $"http://{RedirectHost}:{RedirectPort}{RedirectPath}";

        // 2. Start the loopback listener BEFORE opening the browser so we don't race.
        //    Note: HttpListener prefixes MUST end with '/' and the path must match the
        //    redirect_uri. RedirectPath already ends with '/', so we add it as-is.
        var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        string? code = null;
        string? callbackError = null;
        try
        {
            // 3. Build the consent URL and launch the default browser.
            var authUrl = BuildAuthUrl(redirectUri, challenge);
            OpenBrowser(authUrl);

            // 4. Await the callback. The listener serves a single request (Google's
            //    redirect), then we respond with a friendly "you can close this" page.
            var ctxTask = listener.GetContextAsync();
            var winner = await Task.WhenAny(ctxTask, Task.Delay(Timeout.Infinite, cts.Token));
            if (winner != ctxTask)
            {
                throw new InvalidOperationException(
                    "Timed out waiting for the Google sign-in callback.");
            }

            var ctx = await ctxTask;
            var query = HttpUtility.ParseQueryString(ctx.Request.Url?.Query ?? "");
            code = query.Get("code");
            callbackError = query.Get("error");

            // Respond to the browser so the user sees something useful.
            var responseHtml = BuildCallbackResponseHtml(code, callbackError);
            var responseBytes = Encoding.UTF8.GetBytes(responseHtml);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = responseBytes.Length;
            await ctx.Response.OutputStream.WriteAsync(responseBytes, cts.Token);
            ctx.Response.Close();

            if (!string.IsNullOrEmpty(callbackError))
            {
                throw new InvalidOperationException(
                    $"Google returned an error: {callbackError}");
            }
            if (string.IsNullOrEmpty(code))
            {
                throw new InvalidOperationException(
                    "Google's callback did not include an authorisation code.");
            }

            // 5. Exchange the code for tokens.
            var (refreshToken, accessToken) = await GoogleAuth.ExchangeCodeAsync(
                code!, redirectUri, verifier, cts.Token);

            // 6. Resolve the email so the account can be labelled.
            var email = await GoogleAuth.GetUserEmailAsync(accessToken, cts.Token)
                        ?? throw new InvalidOperationException(
                            "Could not determine the Google account email from the new token.");

            return (email, refreshToken);
        }
        finally
        {
            listener.Stop();
            ((IDisposable)listener).Dispose();
        }
    }

    private static string BuildAuthUrl(string redirectUri, string challenge)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["client_id"] = GoogleAuth.ClientId;
        query["redirect_uri"] = redirectUri;
        query["response_type"] = "code";
        query["scope"] = GoogleAuth.Scopes;
        query["code_challenge"] = challenge;
        query["code_challenge_method"] = "S256";
        query["access_type"] = "offline";       // ask for a refresh token
        query["prompt"] = "consent";            // force consent so refresh_token is returned
        return AuthEndpoint + "?" + query.ToString();
    }

    /// <summary>
    /// RFC 7636: a cryptographically-random string of 43-128 URL-safe chars. We use 32
    /// random bytes (256 bits) base64url-encoded (~43 chars).
    /// </summary>
    private static string GenerateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64Url(bytes);
    }

    /// <summary>S256 challenge = BASE64URL(SHA-256(verifier)).</summary>
    private static string ComputeS256Challenge(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64Url(hash);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static void OpenBrowser(string url)
    {
        try
        {
            // Process.Start with UseShellExecute opens the URL in the default browser.
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch
        {
            // If the browser can't be opened automatically, the caller can show the URL.
            throw new InvalidOperationException(
                "Could not open the default browser. Open this URL manually:\n" + url);
        }
    }

    private static string BuildCallbackResponseHtml(string? code, string? error)
    {
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            return "<html><body style='font-family:sans-serif;text-align:center;padding:40px'>" +
                   "<h2>Sign-in cancelled</h2>" +
                   $"<p>Google reported: {WebUtility.HtmlEncode(error ?? "no code")}</p>" +
                   "<p>You can close this tab and try again.</p>" +
                   "</body></html>";
        }
        return "<html><body style='font-family:sans-serif;text-align:center;padding:40px'>" +
               "<h2>✓ Signed in to Google</h2>" +
               "<p>You can close this tab and return to VibeMeter.</p>" +
               "</body></html>";
    }
}
