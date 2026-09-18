namespace VibeMeter.Core.Security;

/// <summary>
/// Protects a secret so that what lands on disk is not the secret itself.
/// </summary>
/// <remarks>
/// <para>
/// Both methods report failure by returning <see langword="false"/> rather than
/// throwing. Protection can legitimately be unavailable — a roamed Windows
/// profile carries the ciphertext but not the key that opens it, and a
/// non-Windows host has no DPAPI at all — and a caller that has to catch an
/// exception to persist a settings file tends to grow a "just write it in the
/// clear" fallback, which is the very thing this interface exists to prevent.
/// </para>
/// <para>
/// Neither the plaintext nor the protected form is ever logged or included in
/// an error message by an implementation of this interface.
/// </para>
/// </remarks>
public interface ISecretProtector
{
    /// <summary>
    /// Turns <paramref name="plaintext"/> into a value that is safe to write to
    /// a settings file. Returns <see langword="false"/>, with
    /// <paramref name="protectedValue"/> empty, when this machine cannot
    /// protect it — the caller must then decline to store the secret at all.
    /// </summary>
    bool TryProtect(string plaintext, out string protectedValue);

    /// <summary>
    /// Recovers the secret from a value produced by <see cref="TryProtect"/>.
    /// Returns <see langword="false"/>, with <paramref name="plaintext"/>
    /// empty, when the value cannot be opened here — the caller must then treat
    /// the credential as lost and ask for it again.
    /// </summary>
    bool TryUnprotect(string protectedValue, out string plaintext);
}
