using System.Text.Json;

namespace Vfs365.Core.Drive;

/// <summary>
/// A journal record next to each staging file with unsaved changes, so a crash or reboot loses no save: the next start
/// re-queues them for upload (into the original item, with If-Match, so a server-side change since then ends in a conflict copy).
/// One that already uploaded before the crash (same content on the server) isn't sent again. Content the journal doesn't say was
/// on disk (written, not closed) may be zeros after a power loss: it uploads as a "(recovered)" copy instead. Office's own temp and
/// lock files are not journaled.
/// </summary>
public sealed partial class DriveEngine
{
    /// <summary>
    /// Flushed: the staging file was forced to disk after its last write; null in journals before 0.5.0, which flushed at every close,
    /// so their records count as flushed. OriginalPath: renamed away from there to a temp name (<see cref="PendingFile.Alias"/>), so it
    /// goes back there unless a save claims its item.
    /// </summary>
    sealed record JournalRecord(string DriveId, string DrivePath, DriveItemInfo? Base, DateTimeOffset Modified, bool? Flushed = null, string? OriginalPath = null);

    static string JournalOf(string stagingPath) => stagingPath + ".json";

    /// <summary>
    /// Writes, rewrites (call it when Base, the path or Flushed changes) or, for names that never upload, removes the pending file's
    /// journal record. The record is on disk when this returns; only a first record that says nothing was flushed isn't forced there
    /// (nothing was acknowledged yet), since a rewrite replaces one that may stand for a save. With the write gate held, use
    /// <see cref="JournalLater"/>.
    /// </summary>
    void Journal(PendingFile pending)
    {
        var path = JournalOf(pending.StagingPath);
        lock (pending)
        {
            if (StaysLocal(pending))
            {
                if (pending.Journaled)
                {
                    TryDelete(path);
                    pending.Journaled = false;
                }
                return;
            }
            if (!pending.IsLive)
            {
                return;
            }
            var durable = pending.Journaled || pending.Flushed;
            using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, new JournalRecord(pending.DriveId, pending.DrivePath, pending.Base, pending.Modified, pending.Flushed, pending.Alias?.OriginalPath));
                if (durable)
                {
                    file.Flush(flushToDisk: true);
                }
            }
            File.Move(path + ".tmp", path, overwrite: true);
            pending.Journaled = true;
        }
    }

    /// <summary>Removes the journal of content that was uploaded or discarded, so a write of it still running can't put it back.</summary>
    static void Unjournal(PendingFile pending)
    {
        lock (pending)
        {
            TryDelete(JournalOf(pending.StagingPath));
        }
    }

    /// <summary>With the write gate held: the journal is written when the gate is let go (<see cref="ReleaseGate"/>), before the operation returns.</summary>
    void JournalLater(PendingFile pending) => unjournaled.Add(pending);

    /// <summary>
    /// Lets the write gate go, then writes the journals that changed under it, several at a time when many did (a folder move): disk
    /// flushes never hold the gate. Every operation lets it go before it returns, so its journals are on disk by then.
    /// </summary>
    void ReleaseGate()
    {
        PendingFile[]? due = null;
        if (unjournaled.Count > 0)
        {
            due = [.. unjournaled];
            unjournaled.Clear();
        }
        writeGate.Release();
        if (due is not null)
        {
            Parallel.ForEach(due, new ParallelOptions { MaxDegreeOfParallelism = due.Length < 8 ? 1 : 8 }, pending =>
            {
                try
                {
                    Journal(pending);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    Log($"error: writing the journal of {pending.DrivePath}: {e.Message}");
                }
            });
        }
    }

    /// <summary>
    /// Forces the staged content to disk and the journal to say so (at a close, or before an upload is retried), so a restart after a
    /// power loss uploads it into its item. Nothing to do when nothing was written since the last time. Not with the write gate held,
    /// nor while the calling thread holds a write lease.
    /// </summary>
    void MarkFlushed(PendingFile pending)
    {
        if (pending.Flushed)
        {
            return; // a write clears it before it reaches the file
        }
        pending.Writing.EnterWriteLock();
        try
        {
            if (pending.IsLive && !pending.Flushed && FlushToDisk(pending.StagingPath))
            {
                pending.Flushed = true;
                Journal(pending);
            }
        }
        finally
        {
            pending.Writing.ExitWriteLock();
        }
    }

    /// <summary>A write comes: the journal stops saying the content is on disk, before the write can reach the disk.</summary>
    void Unflush(PendingFile pending)
    {
        lock (pending)
        {
            if (pending.Flushed)
            {
                pending.Flushed = false;
                Journal(pending);
            }
        }
    }

    /// <summary>Re-queues changes an earlier run could not upload and removes leftover staging files. Call once before mounting.</summary>
    public int Recover()
    {
        if (!Directory.Exists(options.StagingDirectory))
        {
            return 0;
        }
        var recovered = 0;
        foreach (var journal in Directory.EnumerateFiles(options.StagingDirectory, "*.staging.json"))
        {
            var staging = journal[..^".json".Length];
            try
            {
                if (JsonSerializer.Deserialize<JournalRecord>(File.ReadAllText(journal)) is { } record && File.Exists(staging))
                {
                    var flushed = record.Flushed ?? true;
                    var pending = new PendingFile(record.DriveId, record.DrivePath, staging, record.Base, record.Modified)
                    {
                        Dirty = true,
                        HasWrites = true,
                        Journaled = true,
                        RetryAt = Now,
                        Queued = true,
                        Recovered = true,
                        Flushed = flushed,
                        RecoverAsCopy = !flushed,
                        TempSince = Now,
                        Alias = record.OriginalPath is { } original && record.Base is { } item
                            ? new AliasItem(record.DriveId, record.DrivePath, item, original, Now + options.SettleWindow) : null,
                    };
                    overlay[pending.Key] = pending;
                    recovered++;
                    Log(flushed ? $"recovered unsaved {record.DrivePath}, uploading"
                        : $"recovered unsaved {record.DrivePath}, which was still being written: uploading it as a recovered copy next to it");
                    continue;
                }
            }
            catch (JsonException)
            {
            }
            TryDelete(journal);
        }

        var kept = overlay.Values.OfType<PendingFile>().Select(p => p.StagingPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Kept(string file) => kept.Contains(file) || (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && kept.Contains(file[..^5]));
        foreach (var leftover in Directory.EnumerateFiles(options.StagingDirectory).Where(file => !Kept(file)))
        {
            TryDelete(leftover);
        }
        return recovered;
    }
}
