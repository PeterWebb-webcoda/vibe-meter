using VibeMeter.Publishing;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Offline queue behaviour: FIFO round-trip, bounded capacity dropping the
/// oldest, the bounded rejection budget that keeps a refused snapshot alive
/// without keeping it forever, and durability across a simulated restart.
/// Everything runs in a per-test temp directory so the real user profile is
/// never touched.
/// </summary>
public sealed class OfflineQueueTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "vibemeter-tests",
        Guid.NewGuid().ToString("N"));

    private static readonly DateTimeOffset Base = new(2026, 9, 17, 4, 30, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory on the test machine is harmless.
        }
    }

    [Fact]
    public void EnqueueThenFlush_RoundTripsEveryDocument_OldestFirst()
    {
        var queue = new OfflineQueue(_directory);
        var documents = new[] { "document-1", "document-2", "document-3" };

        for (var index = 0; index < documents.Length; index++)
        {
            Assert.True(queue.Enqueue(documents[index], Base.AddSeconds(index)));
        }

        Assert.Equal(documents.Length, queue.Count);

        var flushed = new List<string>();
        foreach (var path in queue.EnumerateEntryPaths())
        {
            Assert.True(queue.TryReadEntry(path, out var document));
            flushed.Add(document);
            queue.Remove(path);
        }

        // Ordinal file-name order is FIFO order.
        Assert.Equal(documents, flushed);
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void Enqueue_BeyondCapacity_DropsTheOldestEntries()
    {
        var queue = new OfflineQueue(_directory, capacity: 3);

        for (var index = 0; index < 5; index++)
        {
            Assert.True(queue.Enqueue($"document-{index}", Base.AddSeconds(index)));
        }

        Assert.Equal(3, queue.Count);
        Assert.Equal(
            ["document-2", "document-3", "document-4"],
            ReadAll(queue));
    }

    [Fact]
    public void Queue_SurvivesARestart_OverTheSameDirectory()
    {
        var before = new OfflineQueue(_directory);
        before.Enqueue("document-1", Base);
        before.Enqueue("document-2", Base.AddSeconds(1));

        // A fresh process pointing at the same directory sees every entry.
        var restarted = new OfflineQueue(_directory);

        Assert.Equal(2, restarted.Count);
        Assert.Equal(["document-1", "document-2"], ReadAll(restarted));
    }

    [Fact]
    public void Restart_SweepsAbandonedTempFiles_ButKeepsLiveEntries()
    {
        new OfflineQueue(_directory).Enqueue("document-1", Base);
        File.WriteAllText(
            Path.Combine(_directory, "9999999999999999999-crashed.json.tmp"),
            "half-written document");

        var restarted = new OfflineQueue(_directory);

        Assert.Equal(1, restarted.Count);
        Assert.Equal(["document-1"], ReadAll(restarted));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Enqueue_WithIdenticalDocumentAlreadyQueued_ReturnsFalse()
    {
        var queue = new OfflineQueue(_directory);

        Assert.True(queue.Enqueue("document-1", Base));
        Assert.False(queue.Enqueue("document-1", Base));
        Assert.Equal(1, queue.Count);
    }

    [Fact]
    public void RecordRejection_KeepsTheEntry_UntilItsBudgetIsSpent()
    {
        // A refusal may be the API's fault (an older validator after a
        // rollback), so a refused snapshot is kept and re-offered — but only
        // for a bounded number of attempts, so a truly malformed document
        // cannot be retried forever.
        var queue = new OfflineQueue(_directory);
        Assert.True(queue.Enqueue("document-1", Base));

        for (var rejection = 1; rejection < OfflineQueue.MaxRejections; rejection++)
        {
            Assert.True(
                queue.RecordRejection(Single(queue)),
                $"the entry was discarded after only {rejection} rejection(s)");

            Assert.Equal(1, queue.Count);
            Assert.Equal(rejection, OfflineQueue.RejectionsOf(Single(queue)));
            Assert.Equal(["document-1"], ReadAll(queue));
        }

        Assert.False(queue.RecordRejection(Single(queue)));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void RejectionTally_SurvivesARestart()
    {
        var before = new OfflineQueue(_directory);
        before.Enqueue("document-1", Base);
        Assert.True(before.RecordRejection(Single(before)));
        Assert.True(before.RecordRejection(Single(before)));

        // The tally lives in the file name, so a restart - or a crash loop -
        // cannot hand a poison entry a fresh budget.
        var restarted = new OfflineQueue(_directory);

        Assert.Equal(2, OfflineQueue.RejectionsOf(Single(restarted)));
        Assert.Equal(["document-1"], ReadAll(restarted));
    }

    [Fact]
    public void RejectedEntries_KeepTheirPlaceInFifoOrder_AndStayDeduplicated()
    {
        var queue = new OfflineQueue(_directory);
        queue.Enqueue("document-1", Base);
        queue.Enqueue("document-2", Base.AddSeconds(1));
        queue.Enqueue("document-3", Base.AddSeconds(2));

        Assert.True(queue.RecordRejection(queue.EnumerateEntryPaths()[0]));
        Assert.True(queue.RecordRejection(queue.EnumerateEntryPaths()[1]));

        // The fixed-width tick prefix still decides the order, marker or not.
        Assert.Equal(["document-1", "document-2", "document-3"], ReadAll(queue));

        // Re-offering a document that is queued AND rejected must not slip past
        // the identity check and win itself a second, fresh budget.
        Assert.False(queue.Enqueue("document-1", Base));
        Assert.Equal(3, queue.Count);
    }

    [Fact]
    public void RejectionsOf_ReadsZero_ForAnUnmarkedOrUnreadableName()
    {
        // An unreadable tally must never be read as "nearly spent": that would
        // cost the snapshot on the strength of a file name we did not write.
        Assert.Equal(0, OfflineQueue.RejectionsOf("0000000000000000001-v1-abc.json"));
        Assert.Equal(0, OfflineQueue.RejectionsOf("0000000000000000001-v1-abc.rejected.json"));
        Assert.Equal(0, OfflineQueue.RejectionsOf("0000000000000000001-v1-abc.rejected-2.json"));
        Assert.Equal(3, OfflineQueue.RejectionsOf("0000000000000000001-v1-abc.rejected3.json"));
    }

    [Fact]
    public void Capacity_IsNeverBelowOne()
    {
        var queue = new OfflineQueue(_directory, capacity: 0);

        Assert.Equal(1, queue.Capacity);
    }

    private static string Single(OfflineQueue queue) => Assert.Single(queue.EnumerateEntryPaths());

    private static string[] ReadAll(OfflineQueue queue) => queue
        .EnumerateEntryPaths()
        .Select(path =>
        {
            Assert.True(queue.TryReadEntry(path, out var document));
            return document;
        })
        .ToArray();
}
