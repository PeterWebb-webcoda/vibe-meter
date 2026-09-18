using System.Text;
using System.Text.Json;

namespace VibeMeter.Publishing;

/// <summary>
/// Where <see cref="PublishPolicy"/> keeps the two values it remembers between
/// cycles. A seam, not a file, so a test can drive the policy without a disk —
/// both hosts persist their state, because state that does not outlive the
/// object holding it is not a floor at all (see
/// <see cref="DesktopPublishHost.CreatePolicyFor"/>).
/// </summary>
/// <remarks>
/// Every implementation must FAIL OPEN. A store that cannot be read returns
/// <see cref="PublishPolicyState.None"/> and a store that cannot be written
/// swallows it, because the whole of this state is an optimisation: losing it
/// costs one extra row, whereas throwing out of a read would take down a
/// publish cycle over bookkeeping.
/// </remarks>
public interface IPublishPolicyStore
{
    /// <summary>The last recorded state, or <see cref="PublishPolicyState.None"/> if there is none (or it is unreadable).</summary>
    PublishPolicyState Read();

    /// <summary>Records the new state. Best-effort — never throws.</summary>
    void Write(PublishPolicyState state);
}

/// <summary>
/// Keeps the state in memory only, so anything that replaces the object holding
/// it starts from a blank baseline.
/// </summary>
/// <remarks>
/// For tests and for a one-shot process that genuinely has nowhere to write —
/// NOT for a long-running host. A host whose state is forgotten reads back
/// "nothing published yet" and publishes whatever the minimum interval says,
/// which is how the tray app came to write three rows inside two minutes against
/// a 290-second floor: its host, and with it this store, was rebuilt every time
/// the settings were saved. "A restart should publish" is a real requirement,
/// but it belongs in the policy as one bounded allowance — see
/// <c>publishOnStart</c> on <see cref="PublishPolicy"/> — not in an amnesiac
/// store that grants it again on every rebuild.
/// </remarks>
public sealed class InMemoryPublishPolicyStore : IPublishPolicyStore
{
    private PublishPolicyState _state = PublishPolicyState.None;

    public PublishPolicyState Read() => _state;

    public void Write(PublishPolicyState state) => _state = state;
}

/// <summary>
/// Persists the state in ONE small file inside the offline-queue directory the
/// host already owns.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why there.</b> The queue directory is the only path this library is
/// already given by its host: <see cref="OfflineQueue"/> creates it, sweeps it
/// and keeps it. It is also already per-host — the queue's own rules require
/// the agent and the tray app to name separate directories, since entries are
/// coordinated by file name with no locking — and the policy state needs the
/// very same partitioning, because it answers "when did THIS host last write a
/// row". So the state inherits a location, a lifetime and a correct partition
/// without the host configuring, creating, permissioning or cleaning up
/// anything new.
/// </para>
/// <para>
/// <b>Why not the tray app's settings.json.</b> That file is user-facing
/// preferences: hand-editable, read at startup, rewritten by the UI, and
/// roaming with <c>%APPDATA%</c>. Publish bookkeeping written by a background
/// loop every few minutes would race the UI's own saves, and roaming it to
/// another machine would carry a "last published" claim that is simply false
/// there — the row was written by a different host. It is also not a preference:
/// nobody should see it, and nothing breaks if it is deleted.
/// </para>
/// <para>
/// <b>Why the extension is not .json.</b> <see cref="OfflineQueue"/> treats
/// EVERY <c>*.json</c> file in its directory as a queued snapshot, so a file
/// named <c>publish-policy.json</c> would be POSTed to the collection API as if
/// it were one. <see cref="FileName"/> stays clear of both that glob and the
/// queue's <c>*.tmp</c> sweep — pinned by a test, because it is the kind of
/// coupling that would otherwise be rediscovered in production.
/// </para>
/// </remarks>
public sealed class FilePublishPolicyStore : IPublishPolicyStore
{
    /// <summary>Deliberately not a <c>.json</c> name — see the class remarks.</summary>
    public const string FileName = "publish-policy.state";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;

    /// <summary>
    /// Opens (creating the directory if need be) the state file in
    /// <paramref name="directory"/>. Prefer
    /// <see cref="ForQueue"/> so the choice of directory stays the host's one
    /// existing choice.
    /// </summary>
    public FilePublishPolicyStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, FileName);
    }

    /// <summary>The state file for a host's offline queue — the recommended construction.</summary>
    public static FilePublishPolicyStore ForQueue(OfflineQueue queue) => new(queue.DirectoryPath);

    /// <summary>The state file itself — for diagnostics and for tests that need to corrupt or delete it.</summary>
    public string FilePath => _path;

    public PublishPolicyState Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return PublishPolicyState.None;
            }

            return JsonSerializer.Deserialize<PublishPolicyState>(File.ReadAllText(_path), SerializerOptions)
                   ?? PublishPolicyState.None;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Truncated by a crash mid-write, written by a build that shaped the
            // record differently, or momentarily locked. "No state" is the only
            // safe reading: it publishes, which is never wrong - merely one row.
            return PublishPolicyState.None;
        }
    }

    public void Write(PublishPolicyState state)
    {
        var temp = _path + ".writing";
        try
        {
            // Write-then-rename, as the queue does: a crash mid-write leaves the
            // previous state intact rather than a half-written file that reads
            // as "never published" and costs an extra row on every restart.
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(state, SerializerOptions),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A full disk or a locked file must not take down a publish cycle
            // over bookkeeping; the worst case is that the next cycle publishes.
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
