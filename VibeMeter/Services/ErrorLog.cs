using System;
using System.IO;
using System.Text;

namespace VibeMeter.Services;

/// <summary>
/// Appends provider error details to <c>%APPDATA%\VibeMeter\logs\error.log</c>. One line
/// per occurrence with a timestamp, provider id, and message. The file is created on first
/// write and never cleared by the app (roll your own truncation if it grows large).
/// </summary>
public static class ErrorLog
{
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VibeMeter", "logs");

    private static readonly string LogFilePath = Path.Combine(LogDirectory, "error.log");

    /// <summary>Where errors are written (exposed for the UI / "reveal log file" affordances).</summary>
    public static string LogPath => LogFilePath;

    /// <summary>Records a provider error to the log file. Never throws.</summary>
    /// <param name="providerId">The provider's stable id (e.g. "google").</param>
    /// <param name="displayName">Human-friendly provider name for the log line.</param>
    /// <param name="message">The error message from <c>ProviderUsage.ErrorMessage</c>.</param>
    public static void Write(string providerId, string displayName, string? message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"))
                .Append("  [").Append(providerId).Append("] ")
                .Append(displayName)
                .Append(" — ")
                .AppendLine(string.IsNullOrWhiteSpace(message) ? "(no message)" : message)
                .ToString();
            File.AppendAllText(LogFilePath, line, Encoding.UTF8);
        }
        catch
        {
            // Logging must never throw — it would mask the error being reported.
        }
    }
}
