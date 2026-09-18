using System.Globalization;
using System.Text;

namespace VibeMeter.Publishing;

/// <summary>
/// Durable, bounded FIFO of snapshot documents awaiting upload. One UTF-8 file
/// per entry, named <c>{utcTicks:D19}-{idempotencyKey}.json</c> so ordinal file
/// name order is exact FIFO order and the queue survives restarts unchanged.
/// Enqueue writes to a temp file then atomically renames — crash-safe on both
/// Windows (same-volume move) and Linux paths. The idempotency key is a pure
/// function of the document, so if the "delete after send" step is ever missed,
/// the re-sent entry de-duplicates server-side instead of creating a second row.
/// An entry the API has refused outright carries its tally as
/// <c>{utcTicks:D19}-{idempotencyKey}.rejected{n}.json</c> — the count lives in
/// the name so it is as durable as the document and needs no second file or
/// index to fall out of step with; the fixed-width tick prefix keeps FIFO order
/// intact whatever the suffix.
/// SECURITY: entries hold only the mapped snapshot document (ids, states,
/// percentages) — never credentials or raw provider payloads.
/// </summary>
public sealed class OfflineQueue
{
    public const int DefaultCapacity = 500;

    /// <summary>
    /// How many outright rejections one queued snapshot may collect before it
    /// is discarded. The flush attempts each entry at most once per cycle, so
    /// at the default five-minute interval this retains a refused snapshot for
    /// about two hours — long enough to outlast a rolled-back API being rolled
    /// forward again, and short enough that a genuinely malformed document
    /// costs at most this many wasted requests, spread thin, before the agent
    /// gives up on it loudly.
    /// </summary>
    public const int MaxRejections = 24;

    private const string RejectionMarker = ".rejected";

    private readonly string _directory;
    private readonly int _capacity;

    /// <summary>
    /// Opens (creating if need be) the queue in <paramref name="directory"/>.
    /// The directory is deliberately the CALLER's to choose and this library
    /// never derives one: entries are coordinated by file name alone, with no
    /// cross-process locking, so two hosts on one machine — an installed agent
    /// and the tray app, say — must each own a separate directory or they will
    /// race over each other's entries.
    /// </summary>
    public OfflineQueue(string directory, int capacity = DefaultCapacity)
    {
        _directory = directory;
        _capacity = Math.Max(1, capacity);
        Directory.CreateDirectory(directory);
        SweepAbandonedTempFiles();
    }

    public string DirectoryPath => _directory;

    public int Capacity => _capacity;

    public int Count => ListEntryPaths().Count;

    /// <summary>
    /// Persists a snapshot for later upload. Returns false when the identical
    /// document is already queued (its idempotency key is in the file name).
    /// Throws on real I/O failure so the caller can log it — the host loop
    /// decides how to carry on.
    /// </summary>
    public bool Enqueue(string document, DateTimeOffset observedAt)
    {
        var baseName = BaseName(document, observedAt);

        // The identity check has to see through the rejection marker too:
        // without this, re-offering a document that is already queued and
        // refused would add a second copy with a fresh rejection budget.
        if (Directory.EnumerateFiles(_directory, baseName + "*.json").Any())
        {
            return false;
        }

        var finalPath = Path.Combine(_directory, baseName + ".json");
        var tempPath = finalPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, document, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(tempPath, finalPath, overwrite: false);
        }
        catch (IOException) when (File.Exists(finalPath))
        {
            TryDelete(tempPath);
            return false;
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        // Bounded queue: drop the oldest entries rather than grow without limit.
        var entries = ListEntryPaths();
        for (var index = 0; index < entries.Count - _capacity; index++)
        {
            TryDelete(entries[index]);
        }

        return true;
    }

    public IReadOnlyList<string> EnumerateEntryPaths() => ListEntryPaths();

    public bool TryReadEntry(string path, out string document)
    {
        try
        {
            document = File.ReadAllText(path);
            return true;
        }
        catch (IOException)
        {
            // Already sent and deleted by a previous flush, or momentarily locked —
            // skip it; it will be seen again next cycle if it still exists.
            document = "";
            return false;
        }
    }

    /// <summary>
    /// Removes a queued entry, best-effort. A failed delete simply means the
    /// entry is re-sent later and de-duplicated server-side.
    /// </summary>
    public void Remove(string path) => TryDelete(path);

    /// <summary>
    /// How many times the API has already refused this entry outright. Zero for
    /// an entry that has never been rejected, and for any name that does not
    /// carry a readable marker — an unreadable tally must never be treated as
    /// "nearly spent" and cost the snapshot.
    /// </summary>
    public static int RejectionsOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var marker = name.IndexOf(RejectionMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return 0;
        }

        return int.TryParse(
            name[(marker + RejectionMarker.Length)..],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var rejections)
            ? rejections
            : 0;
    }

    /// <summary>
    /// Records one more outright rejection of a queued entry. Returns true when
    /// the entry is retained (its tally rises by one), false when the budget in
    /// <see cref="MaxRejections"/> is spent and the entry has been discarded —
    /// the caller logs that loudly, because it is the one path on which data is
    /// deliberately lost.
    /// </summary>
    public bool RecordRejection(string path)
    {
        var rejections = RejectionsOf(path) + 1;
        if (rejections >= MaxRejections)
        {
            TryDelete(path);
            return false;
        }

        var renamed = Path.Combine(
            _directory,
            $"{StripRejectionMarker(Path.GetFileNameWithoutExtension(path))}{RejectionMarker}{rejections}.json");
        try
        {
            File.Move(path, renamed, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Momentarily locked (a scanner, a racing flush). The tally simply
            // stays where it was and the next cycle tries again; an entry we
            // cannot rename is one we could not have deleted either, so this
            // costs an extra attempt, never the snapshot.
        }

        return true;
    }

    private static string BaseName(string document, DateTimeOffset observedAt) =>
        $"{observedAt.UtcTicks:D19}-{IdempotencyKey.For(document)}";

    private static string StripRejectionMarker(string nameWithoutExtension)
    {
        var marker = nameWithoutExtension.IndexOf(RejectionMarker, StringComparison.Ordinal);
        return marker < 0 ? nameWithoutExtension : nameWithoutExtension[..marker];
    }

    private List<string> ListEntryPaths()
    {
        var entries = new List<string>();
        entries.AddRange(Directory.EnumerateFiles(_directory, "*.json"));
        entries.Sort(StringComparer.Ordinal);
        return entries;
    }

    private void SweepAbandonedTempFiles()
    {
        // Debris from a crash mid-write; live enqueues rename within milliseconds,
        // so any *.tmp present at startup can never become a valid entry.
        foreach (var temp in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            TryDelete(temp);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
