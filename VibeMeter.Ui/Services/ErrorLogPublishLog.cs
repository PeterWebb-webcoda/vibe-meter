using VibeMeter.Publishing;

namespace VibeMeter.Ui.Services;

/// <summary>
/// Routes the publishing library's events into the log the app already keeps,
/// <see cref="ErrorLog"/> — the same file the user is pointed at when a
/// provider card goes wrong, so there is one place to look rather than two.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="Info"/> is dropped.</b> That file is append-only and the
/// app never truncates it. Publishing emits an Info line for every cycle it
/// publishes AND for every cycle the policy judges not worth a row, which on a
/// one-minute refresh is a few hundred lines a day of "everything is fine" — in
/// a file whose entire job is to make a failure findable. Warnings and errors
/// go in, because those are the lines someone opening it is looking for.
/// </para>
/// <para>
/// SECURITY: the library's own contract forbids passing a token, an
/// Authorization header or a raw provider response to any of these, so nothing
/// sensitive reaches the file. The one message that carries a credential-shaped
/// value — the device-code sign-in prompt — carries a verification URL and a
/// user code, never a token.
/// </para>
/// </remarks>
internal sealed class ErrorLogPublishLog : IPublishLog
{
    /// <summary>The provider id these lines are filed under, so they are greppable.</summary>
    public const string Source = "publish";

    public static readonly ErrorLogPublishLog Instance = new();

    private ErrorLogPublishLog()
    {
    }

    public void Info(string message)
    {
        // Deliberately discarded — see the class remarks.
    }

    public void Warn(string message) => ErrorLog.Write(Source, "Publishing", message);

    public void Error(string message) => ErrorLog.Write(Source, "Publishing", message);
}
