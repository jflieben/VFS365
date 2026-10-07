namespace Vfs365.Core.Drive;

/// <summary>Local state that overrides the remote tree at one path until it is uploaded, applied or expires.</summary>
internal abstract class LocalState(string driveId, string drivePath)
{
    public string DriveId { get; set; } = driveId;
    public string DrivePath { get; set; } = drivePath;
    public string Key => DriveEngine.Key(DriveId, DrivePath);
    public string Name => DrivePath[(DrivePath.LastIndexOf('/') + 1)..];
}

/// <summary>Content staged locally. Base is the remote item it will be uploaded into; null for a new file.</summary>
internal sealed class PendingFile(string driveId, string drivePath, string stagingPath, DriveItemInfo? baseItem, DateTimeOffset modified)
    : LocalState(driveId, drivePath)
{
    public string StagingPath { get; } = stagingPath;
    public DriveItemInfo? Base { get; set; } = baseItem;
    public DateTimeOffset Modified { get; set; } = modified;
    public bool Dirty { get; set; }
    public bool HasWrites { get; set; }
    public bool Journaled { get; set; }
    public DateTimeOffset? RetryAt { get; set; }

    /// <summary>
    /// When the file uploads without a close asking for it: after paging writes (memory-mapped files, no close may follow) and for a
    /// file saved again soon after its last upload.
    /// </summary>
    public DateTimeOffset? CommitAt { get; set; }
    public bool Waiting { get; set; }
    public ProgressiveUpload? Progress { get; set; }

    /// <summary>The upload running without the write gate; operations on this file wait for it.</summary>
    public Task? Uploading { get; set; }

    int writes;

    /// <summary>Counts writes, so an upload can tell whether the file changed while it ran.</summary>
    public int Writes => Volatile.Read(ref writes);

    public void Wrote() => Interlocked.Increment(ref writes);
}

/// <summary>An upload that runs while the file is being written. Fragments go only once every byte before them is written.</summary>
internal sealed class ProgressiveUpload(long size, string drivePath)
{
    readonly SortedList<long, long> written = [];

    public long Size { get; } = size;

    /// <summary>Where the session was aimed; a rename since then means starting over.</summary>
    public string DrivePath { get; } = drivePath;

    public SemaphoreSlim Gate { get; } = new(1, 1);
    public UploadSession? Session { get; set; }
    public Task<DriveItemInfo?>? InFlight { get; set; }
    public bool Broken { get; set; }

    /// <summary>Bytes handed to fragments so far.</summary>
    public long Sent { get; set; }

    /// <summary>Bytes written from offset 0 without gaps.</summary>
    public long Contiguous { get; private set; }

    /// <summary>Records a write. False when it changes bytes that were already sent.</summary>
    public bool Add(long start, long end)
    {
        if (start < Sent)
        {
            return false;
        }
        written[start] = Math.Max(end, written.GetValueOrDefault(start));
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var (from, to) in written)
            {
                if (from <= Contiguous && to > Contiguous)
                {
                    Contiguous = to;
                    grew = true;
                }
            }
        }
        foreach (var done in written.Where(range => range.Value <= Contiguous).Select(range => range.Key).ToList())
        {
            written.Remove(done);
        }
        return true;
    }
}

/// <summary>A remote item hidden at its path: deleted (applied at the deadline) or renamed to a temp name (restored at the deadline).</summary>
internal sealed class GoneItem(string driveId, string drivePath, DriveItemInfo item, DateTimeOffset deadline, bool deleteOnExpiry)
    : LocalState(driveId, drivePath)
{
    public DriveItemInfo Item { get; } = item;
    public DateTimeOffset Deadline { get; set; } = deadline;
    public bool DeleteOnExpiry { get; set; } = deleteOnExpiry;
}

/// <summary>A remote item shown under a temp name (Word renames the original away while saving). Claimed once new content went into it.</summary>
internal sealed class AliasItem(string driveId, string drivePath, DriveItemInfo item, string originalPath, DateTimeOffset deadline)
    : LocalState(driveId, drivePath)
{
    public DriveItemInfo Item { get; } = item;
    public string OriginalPath { get; } = originalPath;
    public DateTimeOffset Deadline { get; } = deadline;
    public bool Claimed { get; set; }
}
