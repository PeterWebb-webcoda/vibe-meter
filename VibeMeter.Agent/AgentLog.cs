using System.Globalization;
using VibeMeter.Publishing;

namespace VibeMeter.Agent;

/// <summary>
/// Minimal console logger — UTC timestamps, one line per event, thread-safe.
/// Deliberately dependency-free so the agent carries no extra packages.
/// SECURITY (hard rule): never log the access token, Authorization headers,
/// provider credentials, API keys, or raw provider responses. Only provider
/// ids, states, counts, timings, and HTTP status codes are ever written here.
/// </summary>
internal static class AgentLog
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write(Console.Out, "info", message);

    public static void Warn(string message) => Write(Console.Out, "warn", message);

    public static void Error(string message) => Write(Console.Error, "error", message);

    private static void Write(TextWriter writer, string level, string message)
    {
        lock (Gate)
        {
            writer.Write(DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'Z'", CultureInfo.InvariantCulture));
            writer.Write(" [");
            writer.Write(level);
            writer.Write("] ");
            writer.WriteLine(message);
        }
    }
}

/// <summary>
/// Hands <see cref="AgentLog"/> to the publishing library through its neutral
/// <see cref="IPublishLog"/> seam. The console writer stays here, in the
/// headless host that owns a console; the library says what happened and this
/// decides where it goes.
/// </summary>
internal sealed class AgentPublishLog : IPublishLog
{
    public static readonly AgentPublishLog Instance = new();

    private AgentPublishLog()
    {
    }

    public void Info(string message) => AgentLog.Info(message);

    public void Warn(string message) => AgentLog.Warn(message);

    public void Error(string message) => AgentLog.Error(message);
}
