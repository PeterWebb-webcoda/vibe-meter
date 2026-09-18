using System;
using System.Security.Cryptography;
using System.Text;

namespace VibeMeter.Core.Security;

/// <summary>
/// Protects a secret with Windows DPAPI under the CURRENT USER scope, so the
/// stored value can only be opened again by the same Windows account on the
/// same machine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why DPAPI and not a key of our own.</b> Anything VibeMeter could ship — a
/// constant key, a key derived from the machine name — would be recoverable
/// from this repository, which is public, and would therefore protect the
/// secret from nobody. DPAPI's key is held by Windows against the user's logon
/// credential and is never in the file, the process or the source. The account
/// list this protects lives in the WPF tray app, which is Windows-only, so
/// there is no platform this class has to cover and cannot.
/// </para>
/// <para>
/// <b>What it does and does not buy.</b> It removes the credential from the one
/// file people actually hand around — a settings file pasted into a bug report.
/// It is not a defence against code already running as that user: such code can
/// simply call <c>Unprotect</c> itself. That is the correct boundary for a
/// per-user desktop credential, and the same one the Windows Credential Manager
/// draws.
/// </para>
/// <para>
/// Every failure is reported as <see langword="false"/>. Nothing here puts the
/// plaintext, the protected form or any fragment of either into an exception
/// message or a log line.
/// </para>
/// </remarks>
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>The shared instance; the class holds no state.</summary>
    public static readonly DpapiSecretProtector Instance = new();

    /// <summary>
    /// Optional entropy mixed into the protection. Not a key and not a secret —
    /// it is in this file — but it does mean a blob protected by VibeMeter
    /// cannot be opened by another of the user's applications passing no
    /// entropy, and vice versa, which keeps one app's stored credentials from
    /// being read out by another simply because it runs as the same user.
    /// </summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes("VibeMeter.GoogleAccount.RefreshToken.v1");

    public bool TryProtect(string plaintext, out string protectedValue)
    {
        protectedValue = "";
        if (string.IsNullOrEmpty(plaintext)) return false;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var cipher = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(plaintext), Entropy, DataProtectionScope.CurrentUser);
            protectedValue = Convert.ToBase64String(cipher);
            return true;
        }
        catch (Exception)
        {
            // Deliberately swallowed whole: the message is the only thing we
            // could report, and it is not worth the risk of it quoting input.
            protectedValue = "";
            return false;
        }
    }

    public bool TryUnprotect(string protectedValue, out string plaintext)
    {
        plaintext = "";
        if (string.IsNullOrWhiteSpace(protectedValue)) return false;
        if (!OperatingSystem.IsWindows()) return false;

        try
        {
            var cipher = Convert.FromBase64String(protectedValue);
            var opened = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            plaintext = Encoding.UTF8.GetString(opened);
            return plaintext.Length > 0;
        }
        catch (Exception)
        {
            // A roamed profile, a restored backup or a value protected by
            // another user all land here. The caller re-authenticates.
            plaintext = "";
            return false;
        }
    }
}
