using System.Collections.Concurrent;

namespace Vfs365.Core.Drive;

/// <summary>
/// Copies out of the drive read one file after another and wait for each download. When files of a drive start being read in quick
/// succession, the next small files of the folder being read are downloaded ahead, a few at a time, and never more than
/// ReadAheadFiles that weren't opened yet: a copy that stops early leaves at most that many unused.
/// </summary>
public sealed partial class DriveEngine
{
    const int ReadWalkThreshold = 3;
    const int ReadAheadConcurrency = 4;

    sealed class ReadWalk
    {
        public Queue<DateTimeOffset> Reads { get; } = new();
        public DateTimeOffset ActiveUntil { get; set; }

        /// <summary>Item IDs downloaded ahead (or downloading) that weren't opened yet.</summary>
        public HashSet<string> Ahead { get; } = new(StringComparer.Ordinal);

        public SemaphoreSlim Slots { get; } = new(ReadAheadConcurrency);
        public int Loaded { get; set; }
        public int Used { get; set; }
    }

    readonly ConcurrentDictionary<string, ReadWalk> readWalks = new(StringComparer.Ordinal);

    /// <summary>The content of <paramref name="file"/> is about to be read.</summary>
    void NoteRead(string driveId, FsEntry file)
    {
        if (options.ReadAheadFiles <= 0 || file.ItemId is null)
        {
            return;
        }
        var walk = readWalks.GetOrAdd(driveId, _ => new ReadWalk());
        lock (walk)
        {
            if (walk.Ahead.Remove(file.ItemId))
            {
                walk.Used++;
            }
            walk.Reads.Enqueue(Now);
            while (walk.Reads.TryPeek(out var oldest) && Now - oldest > options.WalkWindow)
            {
                walk.Reads.Dequeue();
            }
            if (Now >= walk.ActiveUntil && walk.Reads.Count < ReadWalkThreshold)
            {
                return;
            }
            walk.ActiveUntil = Now + options.WalkWindow;
        }
        ReadNextAhead(driveId, file, walk);
    }

    /// <summary>Starts downloads of the files after <paramref name="file"/> in its folder's listing order, while there is room.</summary>
    void ReadNextAhead(string driveId, FsEntry file, ReadWalk walk)
    {
        if (PrefetchPaused || !listings.TryGetValue(Key(driveId, ParentOf(file.DrivePath)), out var listing) || !listing.IsLoaded)
        {
            return;
        }
        var next = listing.Items.Values
            .Where(i => !i.IsFolder && i.Size > 0 && i.Size <= options.ReadAheadMaxSize && StringComparer.OrdinalIgnoreCase.Compare(i.Name, file.Name) > 0)
            .OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Take(options.ReadAheadFiles * 2);
        foreach (var item in next)
        {
            lock (walk)
            {
                if (walk.Ahead.Count >= options.ReadAheadFiles)
                {
                    return;
                }
                if (walk.Ahead.Contains(item.Id) || content.Has(driveId, item.Id, item.CTag ?? item.ETag)
                    || overlay.ContainsKey(Key(driveId, JoinDrivePath(ParentOf(file.DrivePath), item.Name))))
                {
                    continue;
                }
                walk.Ahead.Add(item.Id);
                walk.Loaded++;
            }
            _ = ReadAheadAsync(driveId, item, walk);
        }
    }

    async Task ReadAheadAsync(string driveId, DriveItemInfo item, ReadWalk walk)
    {
        RequestPriority.MarkBackground();
        await walk.Slots.WaitAsync();
        try
        {
            if (Now < walk.ActiveUntil && !PrefetchPaused)
            {
                await content.FetchAsync(driveId, item.Id, item.CTag ?? item.ETag, ServedSize(driveId, item), CancellationToken.None, current => ContentChanged(driveId, current));
                return;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log($"reading {item.Name} ahead failed: {e.Message}");
        }
        finally
        {
            walk.Slots.Release();
        }
        lock (walk)
        {
            walk.Ahead.Remove(item.Id); // not fetched: the walk ended or the download failed
        }
    }

    /// <summary>Logs how reading ahead went for copies that ended.</summary>
    void FinishReadWalks()
    {
        foreach (var (driveId, walk) in readWalks)
        {
            int loaded, used;
            lock (walk)
            {
                if (Now < walk.ActiveUntil || walk.Loaded == 0)
                {
                    continue;
                }
                (loaded, used) = (walk.Loaded, walk.Used);
                walk.Ahead.Clear();
                walk.Loaded = walk.Used = 0;
            }
            Log($"copy out of drive {driveId} ended: {loaded} file(s) downloaded ahead, {used} of them opened");
        }
    }
}
