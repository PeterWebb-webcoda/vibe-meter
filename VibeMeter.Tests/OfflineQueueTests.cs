using VibeMeter.Agent;
using Xunit;

namespace VibeMeter.Tests;

/// <summary>
/// Offline queue behaviour: FIFO round-trip, bounded capacity dropping the
/// oldest, and durability across a simulated restart. Everything runs in a
/// per-test temp directory so the real user profile is never touched.
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
    public void Capacity_IsNeverBelowOne()
    {
        var queue = new OfflineQueue(_directory, capacity: 0);

        Assert.Equal(1, queue.Capacity);
    }

    private static string[] ReadAll(OfflineQueue queue) => queue
        .EnumerateEntryPaths()
        .Select(path =>
        {
            Assert.True(queue.TryReadEntry(path, out var document));
            return document;
        })
        .ToArray();
}
