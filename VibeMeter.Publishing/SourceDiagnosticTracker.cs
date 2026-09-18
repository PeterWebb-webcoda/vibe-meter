using VibeMeter.Core;

namespace VibeMeter.Publishing;

/// <summary>
/// Turns a provider's <see cref="ProviderUsage.SourceDiagnostic"/> into a log line the
/// first time it appears, again whenever it changes, once more when it clears — and into
/// nothing at all on every cycle in between.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why once per change and not once per cycle.</b> The tray app refreshes every minute
/// and its error log is append-only and never truncated, so a preferred source that fails
/// for a day would write over a thousand identical lines into a file whose whole job is to
/// make a failure findable. <c>ErrorLogPublishLog</c> drops Info lines for the same reason.
/// </para>
/// <para>
/// <b>Why not once per process.</b> The reason can change — a timeout one hour, a 403 the
/// next — and each reason is a different fault. The recovery is worth a line too: with it,
/// the log tells the whole story (when the live source stopped working, why, and when it
/// came back) rather than leaving the reader to infer the ending from silence.
/// </para>
/// <para>
/// <b>Why it returns a string instead of logging.</b> Each host owns its logger — the
/// agent writes to the console, the tray to its error log — and this library depends on
/// neither. The host asks what, if anything, changed, and writes the answer where it keeps
/// everything else.
/// </para>
/// <para>
/// Safe to call from several threads at once: the agent fetches its providers in parallel
/// and notes each result from the thread that fetched it.
/// </para>
/// </remarks>
public sealed class SourceDiagnosticTracker
{
    private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>
    /// Records this cycle's report for its provider and returns the line to log, or
    /// <see langword="null"/> when nothing about the provider's source situation has changed
    /// since the last report.
    /// </summary>
    /// <remarks>
    /// Only a successful report carries a source situation worth tracking. A report in any
    /// other state — not configured, error, disabled — is the provider's own failure path,
    /// already logged or displayed as such, so it is not commented on here; it simply
    /// forgets whatever was noted before, and the next successful report starts afresh.
    /// </remarks>
    public string? NoteChange(ProviderUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        lock (_gate)
        {
            _current.TryGetValue(usage.ProviderId, out var previous);

            if (usage.State != ProviderState.Ok)
            {
                _current.Remove(usage.ProviderId);
                return null;
            }

            var now = string.IsNullOrWhiteSpace(usage.SourceDiagnostic) ? null : usage.SourceDiagnostic.Trim();
            if (string.Equals(previous, now, StringComparison.Ordinal))
            {
                return null;
            }

            if (now is null)
            {
                _current.Remove(usage.ProviderId);
                return $"Provider '{usage.ProviderId}' is back on its preferred source" +
                       $"{DescribeLabel(usage)}. The earlier note no longer applies: {previous}";
            }

            _current[usage.ProviderId] = now;
            return $"Provider '{usage.ProviderId}' is reading from a fallback source" +
                   $"{DescribeLabel(usage)} because its preferred source is unavailable: {now}";
        }
    }

    private static string DescribeLabel(ProviderUsage usage) =>
        string.IsNullOrWhiteSpace(usage.SourceLabel) ? "" : $" ({usage.SourceLabel})";
}
