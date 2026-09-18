namespace VibeMeter.Publishing;

/// <summary>
/// Where this library writes its one-line events. Three methods and no
/// dependency on any logging framework: the headless agent adapts its console
/// logger to it, and a desktop host adapts whatever it already has (a status
/// bar, a file, nothing at all).
/// </summary>
/// <remarks>
/// SECURITY (hard rule): never pass an access token, an Authorization header,
/// provider credentials, an API key or a raw provider response to any of these.
/// Only provider ids, states, counts, timings and HTTP status codes belong in a
/// message.
/// </remarks>
public interface IPublishLog
{
    /// <summary>Something happened that went to plan.</summary>
    void Info(string message);

    /// <summary>Something was degraded, dropped or deferred, but the run continues.</summary>
    void Warn(string message);

    /// <summary>Something failed. The caller decides whether that ends anything.</summary>
    void Error(string message);
}

/// <summary>
/// Discards every message. For a host that wants the publishing behaviour
/// without its commentary — and so no call site has to null-check the seam.
/// </summary>
public sealed class NullPublishLog : IPublishLog
{
    public static readonly NullPublishLog Instance = new();

    private NullPublishLog()
    {
    }

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }
}
