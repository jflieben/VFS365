using System.Text.Json;

namespace Vfs365.Core.Drive;

/// <summary>
/// A journal record next to each staging file with unsaved changes, so a crash or reboot loses no save: the next start
/// re-queues them for upload (into the original item, with If-Match, so a server-side change since then ends in a conflict copy).
/// One that already uploaded before the crash (same content on the server) isn't sent again. Temp and lock files are not journaled.
/// </summary>
public sealed partial class DriveEngine
{
    sealed record JournalRecord(string DriveId, string DrivePath, DriveItemInfo? Base, DateTimeOffset Modified);

    static string JournalOf(string stagingPath) => stagingPath + ".json";

    /// <summary>
    /// Writes, rewrites (call it when Base or the path changes) or, for temp names, removes the pending file's journal record. The record
    /// is on disk when this returns.
    /// </summary>
    void Journal(PendingFile pending)
    {
        var path = JournalOf(pending.StagingPath);
        if (LocalOnlyNames.IsLocalOnly(pending.Name))
        {
            TryDelete(path);
            pending.Journaled = false;
            return;
        }
        if (!pending.IsLive)
        {
            return;
        }
        using (var file = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, new JournalRecord(pending.DriveId, pending.DrivePath, pending.Base, pending.Modified));
            file.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
        pending.Journaled = true;
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
                    var pending = new PendingFile(record.DriveId, record.DrivePath, staging, record.Base, record.Modified)
                    {
                        Dirty = true,
                        HasWrites = true,
                        Journaled = true,
                        RetryAt = Now,
                        Queued = true,
                        Recovered = true,
                    };
                    overlay[pending.Key] = pending;
                    recovered++;
                    Log($"recovered unsaved {record.DrivePath}, uploading");
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
