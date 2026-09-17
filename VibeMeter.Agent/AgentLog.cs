using System.Globalization;

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
