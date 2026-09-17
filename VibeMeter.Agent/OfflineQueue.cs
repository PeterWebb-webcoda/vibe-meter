using System.Text;
using VibeMeter.Agent.Publishing;

namespace VibeMeter.Agent;

/// <summary>
/// Durable, bounded FIFO of snapshot documents awaiting upload. One UTF-8 file
/// per entry, named <c>{utcTicks:D19}-{idempotencyKey}.json</c> so ordinal file
/// name order is exact FIFO order and the queue survives restarts unchanged.
/// Enqueue writes to a temp file then atomically renames — crash-safe on both
/// Windows (same-volume move) and Linux paths. The idempotency key is a pure
/// function of the document, so if the "delete after send" step is ever missed,
/// the re-sent entry de-duplicates server-side instead of creating a second row.
/// SECURITY: entries hold only the mapped snapshot document (ids, states,
/// percentages) — never credentials or raw provider payloads.
/// </summary>
public sealed class OfflineQueue
{
    public const int DefaultCapacity = 500;

    private readonly string _directory;
    private readonly int _capacity;

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
        var finalPath = Path.Combine(_directory, EntryName(document, observedAt));
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

    private string EntryName(string document, DateTimeOffset observedAt) =>
        $"{observedAt.UtcTicks:D19}-{IdempotencyKey.For(document)}.json";

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
