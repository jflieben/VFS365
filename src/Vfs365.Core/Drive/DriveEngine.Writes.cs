using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vfs365.Core.Drive;

/// <summary>
/// Writes are write-through: a changed file uploads when its writer closes it, and a rename onto a real name is the commit point
/// of a save. Deletes and renames to temp names wait out the settle window, so a save pattern can put its new content into the
/// original item (same ID, versions and sharing). Office's temp and lock files never reach SharePoint; other new files under
/// temp-like names only when they are still there minutes after their close.
/// The write gate orders changes to local state and is held only for that: transfers (uploads, downloads for a write, renames and
/// deletes on the server, due deletes, caching an uploaded file) and disk flushes run without it, and only operations on the same
/// file, or below the same folder, wait for them.
/// </summary>
public sealed partial class DriveEngine
{
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly SemaphoreSlim uploadSlots = new(Math.Max(1, options.UploadConcurrency));

    /// <summary>Due deletes sent at the same time, whichever round of due work started them.</summary>
    readonly SemaphoreSlim deleteSlots = new(DeleteConcurrency);
    readonly Dictionary<PendingFile, Task> backgroundUploads = [];

    /// <summary>Due deletes and uploaded files being cached, until they end.</summary>
    readonly HashSet<Task> backgroundWork = [];

    /// <summary>When each item was last uploaded by a close, for <see cref="DriveEngineOptions.RepeatSaveWindow"/>.</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> recentUploads = new(StringComparer.Ordinal);

    /// <summary>Work running without the write gate, by the keys it changes, until it ends. Guarded by the write gate.</summary>
    readonly Dictionary<string, Transfer> transfers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Transfers on a folder (a delete or move on the server): keys below their prefixes wait too. Guarded by the write gate.</summary>
    readonly List<Transfer> folderTransfers = [];

    /// <summary>Pending files whose journal changed with the write gate held, written when it is let go. Guarded by the write gate.</summary>
    readonly HashSet<PendingFile> unjournaled = [];

    /// <summary>Uploaded content being cached from its staging file, by drive, item and version: reads use the staging file until then.</summary>
    readonly ConcurrentDictionary<string, string> adopting = new(StringComparer.Ordinal);

    /// <summary>
    /// Items saved lately, with the content tags of the versions before the save: a handle opened on one of those reads the save. A
    /// version this engine never had is newer (a colleague's save), and handles on it stay with it.
    /// </summary>
    readonly ConcurrentDictionary<string, SavedItem> saved = new(StringComparer.Ordinal);

    sealed record SavedItem(DriveItemInfo Item, DateTimeOffset At, string[] Replaced);

    /// <summary>The staged content each entry handed out refers to.</summary>
    readonly ConditionalWeakTable<FsEntry, PendingFile> bindings = new();

    static readonly TimeSpan SavedFollowTime = TimeSpan.FromHours(1);
    DateTimeOffset savedPruneAt;

    /// <summary>A new file under a temp-like name uploads when it is still there this long after its close (or the rename to it).</summary>
    static readonly TimeSpan TempUploadDelay = TimeSpan.FromMinutes(5);

    /// <summary>A file closed while other handles still write it uploads at the last close, or this long after the first at the latest.</summary>
    static readonly TimeSpan OtherWritersGrace = TimeSpan.FromMinutes(10);

    /// <summary>Deletes sent at the same time once their settle window passed.</summary>
    const int DeleteConcurrency = 4;

    sealed record WriteTarget(Resolved Resolved, DriveMount Drive, string DriveId)
    {
        public string DrivePath => Resolved.DrivePath;
    }

    /// <summary>
    /// Work at some keys without the write gate. Operations on those keys (and, for a folder, below its prefixes) wait for it to end.
    /// Its keys change when its file or a folder above moves meanwhile.
    /// </summary>
    sealed class Transfer
    {
        readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Done => done.Task;
        public List<string> Keys { get; } = [];
        public List<string> Prefixes { get; } = [];

        /// <summary>The upload of this pending file (or the name sent after it): a rename of a new file or of a folder above goes on meanwhile.</summary>
        public PendingFile? Upload { get; init; }

        /// <summary>Work by item ID or on local files (an upload, a due delete, caching an upload): a move of a folder above doesn't wait for it.</summary>
        public bool MovesAlong { get => Upload is not null || field; init; }

        /// <summary>Called when an operation waits for it.</summary>
        public Action? Waited { get; init; }

        public void End() => done.TrySetResult();
    }

    /// <param name="creator">Kept with a new file until it is uploaded, for the front end: entries for it carry it (<see cref="FsEntry.Creator"/>).</param>
    public async Task<FsEntry> CreateAsync(string path, bool isDirectory, CancellationToken ct, object? creator = null)
    {
        var target = await WritableAsync(path, ct);
        var key = Key(target.DriveId, target.DrivePath);
        await LookAheadAsync(target.DriveId, target.DrivePath, ct);
        // Made before the gate is taken: disk work other files needn't wait for
        var staging = isDirectory ? null : NewStagingFile();
        PendingFile? created = null;
        try
        {
            await writeGate.WaitAsync(ct);
            try
            {
                if (overlay.GetValueOrDefault(key) is PendingFile or AliasItem)
                {
                    throw new FsException(FsError.NameCollision, path); // taken here, whatever its upload does meanwhile
                }
                await SettleAsync(key);
                var local = overlay.GetValueOrDefault(key);
                if (local is PendingFile or AliasItem || (local is null && await LookupAsync(target.DriveId, target.DrivePath, ct) is not null))
                {
                    throw new FsException(FsError.NameCollision, path);
                }

                if (isDirectory)
                {
                    var folder = await TransferAsync(() => api.CreateFolderAsync(target.DriveId, ParentOf(target.DrivePath), NameOf(target.DrivePath), ct), key);
                    Upsert(target.DriveId, target.DrivePath, folder);
                    // Empty, as it was just made: files created in it need no lookups
                    listings[key] = Listing.Loaded([], Now, Now, folder.Id);
                    return ItemEntry(target.Resolved.Path, target.Drive, target.DriveId, target.DrivePath, folder);
                }

                // A file created where one was just deleted takes over that item. Its journal isn't forced to disk: nothing is acknowledged yet.
                created = new PendingFile(target.DriveId, target.DrivePath, staging!, (local as GoneItem)?.Item, Now) { Dirty = true, Writers = 1, Creator = creator };
                overlay[key] = created;
                JournalLater(created);
                return PendingEntry(target.Resolved.Path, target.Drive, created);
            }
            finally
            {
                ReleaseGate();
            }
        }
        catch when (staging is not null && created is null)
        {
            TryDelete(staging);
            throw;
        }
    }

    /// <summary>Stages the file for changes and returns the staging path. Content is copied in unless <paramref name="truncate"/>.</summary>
    public async Task<string> OpenForWriteAsync(FsEntry file, bool truncate, CancellationToken ct) => (await StageAsync(file, truncate, ct)).StagingPath!;

    /// <summary>
    /// Stages the file for changes: content is copied in unless <paramref name="truncate"/>. Returns the staged entry; a handle that
    /// keeps it follows this content (<see cref="Follow"/>) and counts as one of its writers until its close commits. The download runs
    /// without the write gate. An entry handed out for another version than the item holds now stages that version, and its save names
    /// it (If-Match): what the handle read is what it changes.
    /// </summary>
    public async Task<FsEntry> StageAsync(FsEntry file, bool truncate, CancellationToken ct)
    {
        if (file.IsDirectory)
        {
            throw new FsException(FsError.AccessDenied, file.Path);
        }
        var target = await WritableAsync(file.Path, ct);
        await LookAheadAsync(target.DriveId, target.DrivePath, ct);
        await writeGate.WaitAsync(ct);
        try
        {
            var key = Key(target.DriveId, target.DrivePath);
            await SettleAsync(key);
            DriveItemInfo item;
            AliasItem? alias = null;
            switch (overlay.GetValueOrDefault(key))
            {
                case PendingFile pending:
                    if (truncate)
                    {
                        if (pending.Progress is { } progress)
                        {
                            _ = AbandonAsync(pending, progress); // its fragment in flight ends on its own; the file uploads whole
                        }
                        Unflush(pending); // on disk before the truncation can be (rare: an overwrite of a file not uploaded yet)
                        pending.HasWrites = false;
                        using var stream = new FileStream(pending.StagingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        stream.SetLength(0);
                        pending.Dirty = true;
                        pending.Modified = Now;
                    }
                    pending.Writers++;
                    return PendingEntry(target.Resolved.Path, target.Drive, pending);
                case GoneItem:
                    throw new FsException(FsError.NotFound, file.Path);
                case AliasItem aliased:
                    alias = aliased;
                    item = aliased.Item;
                    break;
                default:
                    item = await LookupAsync(target.DriveId, target.DrivePath, ct) ?? throw new FsException(FsError.NotFound, file.Path);
                    if (!bindings.TryGetValue(file, out _) && file.ItemId == item.Id && file.ContentTag is { } read && read != (item.CTag ?? item.ETag))
                    {
                        item = AsRead(item, file);
                    }
                    break;
            }

            var staging = NewStagingFile();
            if (!truncate)
            {
                try
                {
                    await TransferAsync(async () =>
                    {
                        await content.CopyToAsync(target.DriveId, item.Id, item.CTag ?? item.ETag, ServedSize(target.DriveId, item), staging, ct,
                            current => ContentChanged(target.DriveId, current));
                        return true;
                    }, key);
                }
                catch
                {
                    TryDelete(staging);
                    throw;
                }
            }
            var staged = new PendingFile(target.DriveId, target.DrivePath, staging, item, Now) { Dirty = truncate, Alias = alias, Writers = 1 };
            overlay[key] = staged;
            if (truncate)
            {
                JournalLater(staged);
            }
            return PendingEntry(target.Resolved.Path, target.Drive, staged);
        }
        finally
        {
            ReleaseGate();
        }
    }

    /// <summary>
    /// The item as the version an entry was handed out for, when the item holds another one now: an upload into it then names that
    /// version (If-Match), so the newer one ends the save in a conflict copy. The eTag is known for versions this engine saved; else
    /// the cTag stands in for it (<see cref="StandInETag"/>) and the upload compares cTags first (<see cref="UploadIntoAsync"/>).
    /// </summary>
    DriveItemInfo AsRead(DriveItemInfo current, FsEntry read)
    {
        var tag = read.ContentTag!;
        var eTag = saved.TryGetValue(current.Id, out var known) && (known.Item.CTag ?? known.Item.ETag) == tag ? known.Item.ETag : tag;
        return current.CTag is null ? current with { ETag = tag, Size = read.Size } : current with { ETag = eTag, CTag = tag, Size = read.Size };
    }

    /// <summary>A base from <see cref="AsRead"/> whose eTag isn't known: never sent as If-Match.</summary>
    static bool StandInETag(DriveItemInfo item) => item.CTag is not null && item.ETag == item.CTag;

    /// <summary>
    /// What an entry handed out earlier refers to now, for a handle that keeps it. A staged entry follows its content through renames
    /// and into the item it was uploaded as (a conflict copy included); an item entry picks up staged content of the same item, and
    /// this engine's save of it when the entry is for a version before that save. A version still being cached from its staging file
    /// reads from that file. Throws <see cref="FsError.Invalid"/> when the staged content was deleted or replaced.
    /// </summary>
    public FsEntry Follow(FsEntry entry)
    {
        if (entry.IsDirectory || entry.Drive is not { } drive || entry.DriveId is not { } driveId)
        {
            return entry;
        }
        if (bindings.TryGetValue(entry, out var bound))
        {
            if (bound.Result is { } result)
            {
                var path = bound.ResultPath!;
                return Follow(ItemEntry(VolumePath(driveId, path) ?? entry.Path, drive, driveId, path, result));
            }
            if (bound.Discarded)
            {
                throw new FsException(FsError.Invalid, $"{entry.Path} was deleted or replaced while open");
            }
            return bound.DrivePath.Equals(entry.DrivePath, StringComparison.Ordinal)
                ? entry
                : PendingEntry(VolumePath(driveId, bound.DrivePath) ?? entry.Path, drive, bound);
        }
        if (overlay.GetValueOrDefault(Key(driveId, entry.DrivePath)) is PendingFile { IsLive: true } staged && staged.Base?.Id == entry.ItemId)
        {
            return PendingEntry(entry.Path, drive, staged);
        }
        if (entry.ItemId is { } id && saved.TryGetValue(id, out var known) && entry.ContentTag is { } tag
            && tag != (known.Item.CTag ?? known.Item.ETag) && known.Replaced.Contains(tag))
        {
            entry = ItemEntry(entry.Path, drive, driveId, entry.DrivePath, known.Item);
        }
        return Adopting(entry);
    }

    /// <summary>An item entry, reading from the staging file while that version is being cached from it (<see cref="AdoptLater"/>).</summary>
    FsEntry Adopting(FsEntry entry)
    {
        var staging = entry.ItemId is { } id && adopting.TryGetValue(AdoptKey(entry.DriveId!, id, entry.ContentTag), out var file) ? file : null;
        return staging == entry.StagingPath ? entry : new FsEntry
        {
            Path = entry.Path,
            Name = entry.Name,
            IsDirectory = entry.IsDirectory,
            Size = entry.Size,
            Created = entry.Created,
            Modified = entry.Modified,
            ReadOnly = entry.ReadOnly,
            Blocked = entry.Blocked,
            ItemId = entry.ItemId,
            ContentTag = entry.ContentTag,
            StagingPath = staging,
            DriveId = entry.DriveId,
            DrivePath = entry.DrivePath,
            Folder = entry.Folder,
            Drive = entry.Drive,
        };
    }

    static string AdoptKey(string driveId, string itemId, string? contentTag) => $"{driveId}|{itemId}|{contentTag}";

    /// <summary>Remembers that a save of this engine made <paramref name="item"/>, replacing <paramref name="replaced"/> when it was the same item.</summary>
    void Saved(DriveItemInfo item, DriveItemInfo? replaced)
    {
        var before = new List<string>();
        if (saved.TryGetValue(item.Id, out var earlier))
        {
            before.AddRange(earlier.Replaced);
            before.AddRange(new[] { earlier.Item.CTag, earlier.Item.ETag }.OfType<string>());
        }
        if (replaced?.Id == item.Id)
        {
            before.AddRange(new[] { replaced.CTag, replaced.ETag }.OfType<string>());
        }
        var now = item.CTag ?? item.ETag;
        saved[item.Id] = new SavedItem(item, Now, [.. before.Where(tag => tag != now).Distinct(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The change feed saw <paramref name="current"/> (called by <see cref="ApplyCore"/>): another version than this engine saved or
    /// replaced is newer, so handles on older versions no longer follow to the save.
    /// </summary>
    void SeenVersion(DriveItemInfo current)
    {
        if (saved.TryGetValue(current.Id, out var known) && (current.CTag ?? current.ETag) is { } tag && tag != (known.Item.CTag ?? known.Item.ETag)
            && !known.Replaced.Contains(tag) && tag != known.Item.ETag)
        {
            saved.TryRemove(new KeyValuePair<string, SavedItem>(current.Id, known));
        }
    }

    /// <summary>
    /// Taken around each write through a handle and the <see cref="MarkDirty"/> after it, so an upload can't end the staging in between
    /// and lose the write. Null when the entry's staged content is gone: follow it and stage again. Dispose on the same thread.
    /// </summary>
    public IDisposable? BeginWrite(FsEntry file)
    {
        if (!bindings.TryGetValue(file, out var pending))
        {
            return null;
        }
        pending.Writing.EnterReadLock();
        if (pending.IsLive)
        {
            Unflush(pending);
            return new WriteLease(pending);
        }
        pending.Writing.ExitReadLock();
        return null;
    }

    sealed class WriteLease(PendingFile pending) : IDisposable
    {
        int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0)
            {
                pending.Writing.ExitReadLock();
            }
        }
    }

    public void MarkDirty(FsEntry file)
    {
        if (PendingOf(file) is { } pending)
        {
            pending.Wrote();
            pending.Dirty = true;
            pending.Modified = Now;
            if (pending.Flushed)
            {
                Unflush(pending);
            }
            else if (!pending.Journaled && !StaysLocal(pending))
            {
                Journal(pending);
            }
        }
    }

    /// <summary>For writes no close may follow (paging I/O of a mapped file): uploads the file when no write came for <see cref="DriveEngineOptions.PagingCommitDelay"/>.</summary>
    public void CommitLater(FsEntry file)
    {
        if (PendingOf(file) is not { } pending || StaysLocal(pending))
        {
            return;
        }
        if (TempLike(pending))
        {
            pending.TempSince = Now;
            pending.CommitAt = Now + TempUploadDelay;
        }
        else
        {
            pending.CommitAt = Now + options.PagingCommitDelay;
        }
    }

    /// <summary>
    /// Uploads a changed file when its writer closes it. Close can't fail, so a lock or conflict ends in a conflict copy. A close while
    /// other handles still write the file only makes what was written durable: the last one commits. New files go to the background
    /// uploads when that is on; a file saved again within RepeatSaveWindow uploads at its end; a new file under a temp-like name uploads
    /// only when it is still there minutes later. What was written is on disk when this returns, flushed without the write gate.
    /// </summary>
    public Task CommitAsync(string path, CancellationToken ct) => CommitAsync(path, null, ct);

    /// <summary>
    /// <see cref="CommitAsync(string, CancellationToken)"/> for the content a writing handle's entry (<paramref name="handle"/>, as staged
    /// or followed) wrote, wherever it went since, and never another file's that took its name or item meanwhile. Nothing to do when
    /// that content was uploaded or discarded already.
    /// </summary>
    public Task CommitAsync(FsEntry handle, CancellationToken ct) =>
        !bindings.TryGetValue(handle, out var bound) ? CommitAsync(handle.Path, null, ct)
        : bound.IsLive ? CommitAsync(null, bound, ct)
        : Task.CompletedTask;

    async Task CommitAsync(string? path, PendingFile? only, CancellationToken ct)
    {
        string? key = null;
        if (only is null)
        {
            if (ns.Resolve(path!) is not { Drive: { } drive } resolved || resolved.DrivePath.Length == 0)
            {
                return;
            }
            key = Key(await drive.GetDriveIdAsync(ct), resolved.DrivePath);
        }
        PendingFile? flush = null;
        var queue = false;
        await writeGate.WaitAsync(ct);
        try
        {
            if (only is not null)
            {
                await SettleOnAsync(only);
            }
            else
            {
                await SettleAsync(key!);
            }
            if (overlay.GetValueOrDefault(key ?? only!.Key) is not PendingFile pending || (only is not null && !ReferenceEquals(pending, only)))
            {
                return;
            }
            if (pending.Writers > 1)
            {
                // Other handles still write it: the last close commits
                pending.Writers--;
                if (pending.Dirty && !StaysLocal(pending))
                {
                    flush = pending;
                    pending.CommitAt ??= Now + OtherWritersGrace;
                }
                return;
            }
            pending.Writers = 0;
            if (!pending.Dirty || StaysLocal(pending))
            {
                return;
            }
            pending.Rejected = false;
            flush = pending;
            if (TempLike(pending))
            {
                // A save pattern renames or deletes it within seconds; still there by then, it is a real file
                pending.TempSince = Now;
                pending.CommitAt = Now + TempUploadDelay;
                pending.RetryAt = null;
                return;
            }
            pending.Queued = true;
            if (pending.Alias is { } aside && aside.Deadline > Now)
            {
                // A renamed-away original: a save may claim its item any moment, so it uploads when that settled
                CommitBy(pending, aside.Deadline);
                return;
            }
            if (pending.Base is null && pending.Progress is null && options.UploadNewFilesInBackground)
            {
                queue = true; // journaled since it was created
                return;
            }
            if (pending.Base is { } baseItem && options.RepeatSaveWindow > TimeSpan.Zero && recentUploads.TryGetValue(baseItem.Id, out var last)
                && Now - last < options.RepeatSaveWindow)
            {
                CommitBy(pending, last + options.RepeatSaveWindow);
                return;
            }
            await UploadPendingAsync(pending, ct);
        }
        finally
        {
            ReleaseGate();
            if (flush is not null)
            {
                MarkFlushed(flush); // nothing to do when it uploaded meanwhile
                if (queue)
                {
                    QueueUpload(flush);
                }
            }
        }
    }

    /// <summary>Uploads the file by <paramref name="at"/> without a close asking for it; an earlier time set already stays.</summary>
    static void CommitBy(PendingFile pending, DateTimeOffset at) => pending.CommitAt = pending.CommitAt is { } earlier && earlier < at ? earlier : at;

    /// <summary>
    /// Called with the write gate held. While work runs at one of the keys (or on a folder above one), lets the gate go until it ended,
    /// so the caller sees its outcome. A due delete waited for goes ahead at once.
    /// </summary>
    Task SettleAsync(params string[] keys) => SettleExceptAsync(null, keys);

    /// <summary>Like <see cref="SettleAsync"/>, but not for the work <paramref name="skip"/> names.</summary>
    async Task SettleExceptAsync(Func<Transfer, bool>? skip, params string[] keys)
    {
        while (Blocking(keys, skip) is { } transfer)
        {
            await AwaitWithoutGateAsync(transfer);
        }
    }

    /// <summary>Like <see cref="SettleAsync"/> for a pending file, at its path then, which may change while it waits.</summary>
    async Task SettleOnAsync(PendingFile pending)
    {
        while (Blocking([pending.Key], null) is { } transfer)
        {
            await AwaitWithoutGateAsync(transfer);
        }
    }

    /// <summary>Like <see cref="SettleAsync"/>, for every key below a folder's (<paramref name="prefix"/> ends with '/').</summary>
    async Task SettleBelowAsync(string prefix, Func<Transfer, bool>? skip = null)
    {
        while (BlockingBelow(prefix, skip) is { } transfer)
        {
            await AwaitWithoutGateAsync(transfer);
        }
    }

    Transfer? Blocking(string[] keys, Func<Transfer, bool>? skip)
    {
        foreach (var key in keys)
        {
            if (transfers.TryGetValue(key, out var transfer) && (skip is null || !skip(transfer)))
            {
                return transfer;
            }
        }
        foreach (var transfer in folderTransfers)
        {
            if ((skip is null || !skip(transfer)) && transfer.Prefixes.Any(prefix => keys.Any(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))))
            {
                return transfer;
            }
        }
        return null;
    }

    Transfer? BlockingBelow(string prefix, Func<Transfer, bool>? skip) =>
        transfers.FirstOrDefault(t => t.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (skip is null || !skip(t.Value))).Value
        ?? folderTransfers.FirstOrDefault(t => (skip is null || !skip(t))
            && t.Prefixes.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || prefix.StartsWith(p, StringComparison.OrdinalIgnoreCase)));

    async Task AwaitWithoutGateAsync(Transfer transfer)
    {
        transfer.Waited?.Invoke();
        ReleaseGate();
        try
        {
            await transfer.Done;
        }
        finally
        {
            await writeGate.WaitAsync(CancellationToken.None);
        }
    }

    /// <summary>Records work at its keys and folder prefixes. A key that has work already keeps it. Called with the write gate held.</summary>
    void Register(Transfer transfer, string[] keys, string[] prefixes)
    {
        foreach (var key in keys)
        {
            if (transfers.TryAdd(key, transfer))
            {
                transfer.Keys.Add(key);
            }
        }
        if (prefixes.Length > 0)
        {
            transfer.Prefixes.AddRange(prefixes);
            folderTransfers.Add(transfer);
        }
    }

    void Unregister(Transfer transfer)
    {
        foreach (var key in transfer.Keys)
        {
            if (transfers.TryGetValue(key, out var current) && current == transfer)
            {
                transfers.Remove(key);
            }
        }
        transfer.Keys.Clear();
        folderTransfers.Remove(transfer);
    }

    /// <summary>Its file moved: the work now holds up operations at the new key. Called with the write gate held.</summary>
    void Rekey(Transfer transfer, string from, string to)
    {
        if (transfers.TryGetValue(from, out var current) && current == transfer)
        {
            transfers.Remove(from);
            transfer.Keys.Remove(from);
        }
        if (transfers.TryAdd(to, transfer))
        {
            transfer.Keys.Add(to);
        }
    }

    /// <summary>
    /// Runs a transfer without the write gate: called with it held, returns with it held. Operations on the keys wait for it
    /// (<see cref="SettleAsync"/>) and due work skips them, so their local state is as before when it returns.
    /// </summary>
    Task<T> TransferAsync<T>(Func<Task<T>> work, params string[] keys) => TransferAsync(work, new Transfer(), keys);

    async Task<T> TransferAsync<T>(Func<Task<T>> work, Transfer transfer, string[] keys, params string[] prefixes)
    {
        Register(transfer, keys, prefixes);
        ReleaseGate();
        try
        {
            return await work();
        }
        finally
        {
            await writeGate.WaitAsync(CancellationToken.None);
            Unregister(transfer);
            transfer.End();
        }
    }

    /// <summary>
    /// Looks a path up before the write gate is taken, so a lookup that isn't cached doesn't hold up other files' saves; the lookup
    /// with the gate held then answers from the cache.
    /// </summary>
    async Task LookAheadAsync(string driveId, string drivePath, CancellationToken ct)
    {
        if (drivePath.Length > 0 && !overlay.ContainsKey(Key(driveId, drivePath)))
        {
            await LookupAsync(driveId, drivePath, ct);
        }
    }

    /// <summary>Uploads the file in the background, UploadConcurrency at a time; once per file at a time.</summary>
    void QueueUpload(PendingFile pending)
    {
        lock (backgroundUploads)
        {
            if (backgroundUploads.TryGetValue(pending, out var running) && !running.IsCompleted)
            {
                return;
            }
            var upload = Task.Run(() => BackgroundUploadAsync(pending));
            backgroundUploads[pending] = upload;
            _ = upload.ContinueWith(done =>
            {
                lock (backgroundUploads)
                {
                    if (backgroundUploads.TryGetValue(pending, out var current) && current == done)
                    {
                        backgroundUploads.Remove(pending);
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>Background work kept until it ends, for <see cref="WaitForUploadsAsync"/>.</summary>
    void Track(Task work)
    {
        lock (backgroundWork)
        {
            backgroundWork.Add(work);
        }
        _ = work.ContinueWith(done =>
        {
            lock (backgroundWork)
            {
                backgroundWork.Remove(done);
            }
        }, TaskScheduler.Default);
    }

    /// <remarks>Not background priority for the request budget: this is the user's data, not work done ahead.</remarks>
    async Task BackgroundUploadAsync(PendingFile pending)
    {
        await uploadSlots.WaitAsync();
        try
        {
            await writeGate.WaitAsync();
            try
            {
                await SettleOnAsync(pending);
                if (ReferenceEquals(overlay.GetValueOrDefault(pending.Key), pending) && pending.Dirty)
                {
                    await UploadPendingAsync(pending, CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                Log($"error: upload of {pending.DrivePath} failed: {e.Message}");
                pending.RetryAt ??= Now + RetryDelay(++pending.Failures);
                if (!pending.Waiting)
                {
                    pending.Waiting = true;
                    Notify(NoticeKind.UploadWaiting, pending.DriveId, pending.DrivePath, e.Message);
                }
            }
            finally
            {
                ReleaseGate();
            }
            if (pending.IsLive)
            {
                MarkFlushed(pending); // before it is retried: changes nobody closed included
            }
        }
        finally
        {
            uploadSlots.Release();
        }
    }

    /// <summary>Background uploads, due deletes and caching of uploads queued or running.</summary>
    Task[] RunningUploads()
    {
        Task[] work;
        lock (backgroundWork)
        {
            work = [.. backgroundWork];
        }
        lock (backgroundUploads)
        {
            return [.. backgroundUploads.Values, .. work];
        }
    }

    /// <summary>Throws when the entry may not be deleted.</summary>
    public async Task CanDeleteAsync(FsEntry entry, CancellationToken ct)
    {
        await WritableAsync(entry.Path, ct);
        if (entry.IsDirectory && (await ListAsync(entry, ct)).Count > 0)
        {
            throw new FsException(FsError.DirectoryNotEmpty, entry.Path);
        }
    }

    /// <summary>
    /// Folders go at once (without the write gate; operations below them wait); files are hidden now and deleted after the settle window
    /// unless a save claims them.
    /// </summary>
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var target = await WritableAsync(path, ct);
        await LookAheadAsync(target.DriveId, target.DrivePath, ct);
        await writeGate.WaitAsync(ct);
        try
        {
            var key = Key(target.DriveId, target.DrivePath);
            await SettleAsync(key);
            switch (overlay.GetValueOrDefault(key))
            {
                case PendingFile { Alias: { } staged } pending:
                    // A renamed-away original (opened for writing, or renamed away with unsaved changes): deleted like the alias it is
                    DropLocal(pending);
                    DeleteAlias(staged);
                    return;
                case PendingFile pending:
                    DropLocal(pending);
                    if (pending.Base is { } baseItem)
                    {
                        overlay[key] = new GoneItem(target.DriveId, target.DrivePath, baseItem, Now + options.SettleWindow, deleteOnExpiry: true);
                    }
                    return;
                case AliasItem alias:
                    DropLocal(alias);
                    DeleteAlias(alias);
                    return;
                case GoneItem:
                    return;
            }

            var item = await LookupAsync(target.DriveId, target.DrivePath, ct) ?? throw new FsException(FsError.NotFound, path);
            if (item.IsFolder)
            {
                // It changes everything below it: operations there wait for it
                var below = Key(target.DriveId, target.DrivePath + "/");
                await TransferAsync(async () =>
                {
                    await api.DeleteAsync(target.DriveId, item.Id, ct);
                    return true;
                }, new Transfer(), [key], below);
                foreach (var deleted in overlay.Values.Where(l => l is GoneItem or AliasItem && l.Key.StartsWith(below, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    DropLocal(deleted); // went to the recycle bin with the folder
                }
                Invalidate(target.DriveId, target.DrivePath);
                Forget(target.DriveId, target.DrivePath);
                Log($"deleted folder {target.DrivePath}");
                return;
            }
            overlay[key] = new GoneItem(target.DriveId, target.DrivePath, item, Now + options.SettleWindow, deleteOnExpiry: true);
        }
        finally
        {
            ReleaseGate();
        }
    }

    public async Task RenameAsync(string source, string destination, bool replace, CancellationToken ct)
    {
        var from = await WritableAsync(source, ct);
        var to = ns.Resolve(destination);
        if (to?.Drive is null || to.DrivePath.Length == 0)
        {
            throw new FsException(FsError.AccessDenied, destination);
        }
        if (!ReferenceEquals(to.Drive, from.Drive))
        {
            throw new FsException(FsError.NotSameDevice, destination);
        }
        if (options.DatabaseFiles != DatabaseFilePolicy.Allow && DatabaseFiles.IsDatabase(NameOf(to.DrivePath)))
        {
            throw new FsException(FsError.ReadOnly, $"{destination} is a multi-user database file, which policy keeps read-only");
        }

        var driveId = from.DriveId;
        var fromKey = Key(driveId, from.DrivePath);
        var toKey = Key(driveId, to.DrivePath);
        var sameName = from.DrivePath.Equals(to.DrivePath, StringComparison.OrdinalIgnoreCase);
        var toTemp = LocalOnlyNames.IsTempLike(NameOf(to.DrivePath));
        await LookAheadAsync(driveId, from.DrivePath, ct);
        if (!sameName)
        {
            await LookAheadAsync(driveId, to.DrivePath, ct);
        }
        if (!ParentOf(to.DrivePath).Equals(ParentOf(from.DrivePath), StringComparison.OrdinalIgnoreCase))
        {
            await LookAheadAsync(driveId, ParentOf(to.DrivePath), ct); // the folder a file not uploaded yet moves into
        }
        (PendingFile? Moved, bool Queue) after = (null, false);
        await writeGate.WaitAsync(ct);
        try
        {
            // A new file going to a free real name moves along with its upload (RenamePendingAsync), so that isn't waited for
            bool OwnUpload(Transfer t) => !sameName && !toTemp && options.UploadNewFilesInBackground
                && t.Upload is { Base: null, Alias: null } upload && ReferenceEquals(overlay.GetValueOrDefault(fromKey), upload);
            if (!replace && !sameName && overlay.GetValueOrDefault(toKey) is PendingFile or AliasItem)
            {
                throw new FsException(FsError.NameCollision, destination); // taken here, whatever its upload does meanwhile
            }
            LocalState? fromLocal, toLocal;
            DriveItemInfo? toRemote, toItem;
            while (true)
            {
                await SettleExceptAsync(OwnUpload, fromKey, toKey);
                fromLocal = overlay.GetValueOrDefault(fromKey);
                toLocal = sameName ? null : overlay.GetValueOrDefault(toKey);
                toRemote = sameName || toLocal is not null ? null : await LookupAsync(driveId, to.DrivePath, ct);
                toItem = toLocal switch
                {
                    GoneItem gone => gone.Item,
                    PendingFile pending => pending.Base,
                    AliasItem alias => alias.Item,
                    _ => toRemote,
                };
                if (toItem is not null && Blocking([fromKey], null) is not null)
                {
                    // Its upload runs and the name is taken: this rename can't go along with it, so it waits after all
                    await SettleAsync(fromKey, toKey);
                    continue;
                }
                if (fromLocal is AliasItem { OriginalPath: var original } && Blocking([Key(driveId, original)], null) is not null)
                {
                    // Work at the renamed-away original's own name (its item is there on the server), which this rename changes too
                    await SettleAsync(Key(driveId, original));
                    continue;
                }
                break;
            }
            if (!replace && (toLocal is PendingFile or AliasItem || toRemote is not null))
            {
                throw new FsException(FsError.NameCollision, destination);
            }
            // An item is at the destination on the server (listed, hidden or changed here), not just a local name
            var onServer = toLocal switch
            {
                null => toRemote is not null,
                GoneItem => true,
                PendingFile { Base: not null, Alias: null } => true,
                _ => false,
            };

            switch (fromLocal)
            {
                case PendingFile pending:
                    after = await RenamePendingAsync(pending, to.DrivePath, toItem, toLocal, onServer, ct);
                    return;
                case AliasItem alias:
                    await RenameAliasAsync(alias, to.DrivePath, toItem, toLocal, onServer, ct);
                    return;
                case GoneItem:
                    throw new FsException(FsError.NotFound, source);
            }

            var item = await LookupAsync(driveId, from.DrivePath, ct) ?? throw new FsException(FsError.NotFound, source);
            if (!item.IsFolder && !sameName && toTemp && !onServer)
            {
                // Saving: the original steps aside under a temp name. Kept local; restored unless new content claims it.
                DropLocal(toLocal);
                overlay[fromKey] = new GoneItem(driveId, from.DrivePath, item, Now + options.SettleWindow, deleteOnExpiry: false);
                overlay[toKey] = new AliasItem(driveId, to.DrivePath, item, from.DrivePath, Now + options.SettleWindow);
                return;
            }
            if (!item.IsFolder && toItem is not null && toItem.Id != item.Id)
            {
                // Replacing a file: its content goes into the destination item, which keeps its identity
                var file = NewStagingFile();
                DriveItemInfo updated;
                try
                {
                    updated = await TransferAsync(async () =>
                    {
                        await content.CopyToAsync(driveId, item.Id, item.CTag ?? item.ETag, ServedSize(driveId, item), file, ct, current => ContentChanged(driveId, current));
                        return await UploadIntoAsync(driveId, toItem, file, ct);
                    }, fromKey, toKey);
                }
                catch
                {
                    TryDelete(file);
                    throw;
                }
                DropLocal(toLocal);
                Claimed(driveId, updated);
                Saved(updated, toItem);
                Upsert(driveId, to.DrivePath, updated);
                overlay[fromKey] = new GoneItem(driveId, from.DrivePath, item, Now + options.SettleWindow, deleteOnExpiry: true);
                AdoptLater(driveId, updated, file, toKey);
                Log($"replaced {to.DrivePath} with {from.DrivePath}");
                return;
            }
            if (item.IsFolder && toItem is not null)
            {
                throw new FsException(FsError.AccessDenied, destination);
            }
            if (item.IsFolder)
            {
                await MoveFolderAsync(driveId, item, from.DrivePath, to.DrivePath, ct);
                return;
            }

            var moved = await TransferAsync(async () => await api.MoveAsync(driveId, item.Id, await FolderIdAsync(driveId, ParentOf(to.DrivePath), ct), NameOf(to.DrivePath), ct),
                fromKey, toKey);
            Forget(driveId, from.DrivePath);
            Upsert(driveId, to.DrivePath, moved);
            Log($"renamed {from.DrivePath} to {to.DrivePath} ({moved.Id})");
        }
        finally
        {
            ReleaseGate();
            if (after.Moved is { } file)
            {
                // Acknowledged before its upload: on disk first (a file closed under a local-only name never was)
                MarkFlushed(file);
                if (after.Queue)
                {
                    QueueUpload(file);
                }
            }
        }
    }

    /// <summary>
    /// Moves or renames a folder: it changes everything below it, so operations there wait for the move, without the write gate.
    /// Uploads below go on (into items by ID, new files into their folder by its ID) and their local state and work move to the new
    /// path; streaming uploads below start over, since they were aimed at the old one.
    /// </summary>
    async Task MoveFolderAsync(string driveId, DriveItemInfo folder, string from, string to, CancellationToken ct)
    {
        var below = Key(driveId, from + "/");
        var targetBelow = Key(driveId, to + "/");
        await SettleBelowAsync(below, t => t.MovesAlong);
        await SettleBelowAsync(targetBelow);
        foreach (var pending in overlay.Values.OfType<PendingFile>().Where(p => p.Key.StartsWith(below, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            if (pending.Progress is { } progress)
            {
                _ = AbandonAsync(pending, progress);
            }
        }

        var moved = await TransferAsync(async () => await api.MoveAsync(driveId, folder.Id, await FolderIdAsync(driveId, ParentOf(to), ct), NameOf(to), ct),
            new Transfer(), [Key(driveId, from), Key(driveId, to)], below, targetBelow);
        Invalidate(driveId, from);
        Forget(driveId, from);
        Upsert(driveId, to, moved);
        string Moved(string path) => to + path[from.Length..];
        bool Below(string path) => path.StartsWith(from + "/", StringComparison.OrdinalIgnoreCase);
        foreach (var alias in overlay.Values.OfType<AliasItem>().Concat(overlay.Values.OfType<PendingFile>().Select(p => p.Alias).OfType<AliasItem>()).ToList())
        {
            if (alias.DriveId == driveId && Below(alias.OriginalPath))
            {
                alias.OriginalPath = Moved(alias.OriginalPath);
            }
        }
        foreach (var uploading in overlay.Values.OfType<PendingFile>().Where(p => p.DriveId == driveId && p.ServerPath is { } at && Below(at)).ToList())
        {
            uploading.ServerPath = Moved(uploading.ServerPath!);
        }
        var count = 0;
        foreach (var local in overlay.Values.Where(l => l.Key.StartsWith(below, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Move(local, Moved(local.DrivePath));
            count++;
        }
        foreach (var (key, running) in transfers.Where(t => t.Key.StartsWith(below, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            Rekey(running, key, targetBelow + key[below.Length..]);
        }
        Log($"renamed {from} to {to} ({moved.Id}){(count > 0 ? $", with {count} file(s) not uploaded yet" : "")}");
    }

    /// <summary>Puts local state under another path; its journal follows when the write gate is let go. Nothing may be at that path.</summary>
    void Move(LocalState local, string drivePath)
    {
        overlay.TryRemove(new KeyValuePair<string, LocalState>(local.Key, local));
        local.DrivePath = drivePath;
        overlay[local.Key] = local;
        if (local is PendingFile pending)
        {
            JournalLater(pending);
        }
    }

    /// <summary>
    /// Applies deletes whose settle window passed (in the background), restores originals nothing claimed and starts uploads that are
    /// due (retries, deferred commits). With <paramref name="all"/> everything is due, changed files nobody closed included, and this
    /// waits for every upload and delete (unmount). A failure stays with its file: it never stops the rest.
    /// </summary>
    public async Task ProcessDueAsync(bool all, CancellationToken ct)
    {
        await writeGate.WaitAsync(ct);
        try
        {
            foreach (var old in recentUploads.Where(r => Now - r.Value >= options.RepeatSaveWindow).Select(r => r.Key).ToList())
            {
                recentUploads.TryRemove(old, out _);
            }
            if (Now >= savedPruneAt)
            {
                savedPruneAt = Now + TimeSpan.FromMinutes(1);
                foreach (var old in saved.Where(s => Now - s.Value.At >= SavedFollowTime).Select(s => s.Key).ToList())
                {
                    saved.TryRemove(old, out _);
                }
            }
            foreach (var local in overlay.Values.ToList())
            {
                if (!all)
                {
                    ct.ThrowIfCancellationRequested();
                }
                if (!ReferenceEquals(overlay.GetValueOrDefault(local.Key), local) || transfers.ContainsKey(local.Key))
                {
                    continue;
                }
                if (local is GoneItem { DeleteOnExpiry: true } gone && (all || gone.Deadline <= Now))
                {
                    StartDelete(gone, all);
                    continue;
                }
                try
                {
                    ProcessOne(local, all);
                }
                catch (Exception e)
                {
                    Log($"error: {local.DrivePath}: {e.Message}");
                }
            }
        }
        finally
        {
            ReleaseGate();
        }
        if (all)
        {
            await WaitForUploadsAsync();
        }
    }

    /// <summary>One item of <see cref="ProcessDueAsync"/>, with the write gate held.</summary>
    void ProcessOne(LocalState local, bool all)
    {
        switch (local)
        {
            case AliasItem alias when all || alias.Deadline <= Now:
                DropLocal(alias);
                if (!alias.Claimed && overlay.GetValueOrDefault(Key(alias.DriveId, alias.OriginalPath)) is GoneItem { DeleteOnExpiry: false } original)
                {
                    DropLocal(original);
                }
                break;
            case GoneItem { DeleteOnExpiry: false } restore when all || restore.Deadline <= Now:
                DropLocal(restore);
                break;
            case PendingFile pending:
                if (pending.Alias is { } aside)
                {
                    if (!all && aside.Deadline > Now)
                    {
                        break; // a renamed-away original: its save settles first
                    }
                    SettleAside(pending, aside);
                    if (!ReferenceEquals(overlay.GetValueOrDefault(pending.Key), pending))
                    {
                        break;
                    }
                }
                if (!pending.Dirty || pending.Rejected)
                {
                    break;
                }
                if (pending.RetryAt is { } retryAt)
                {
                    if (all || retryAt <= Now)
                    {
                        pending.RetryAt = null;
                        pending.Queued = true;
                        QueueUpload(pending);
                    }
                }
                else if (pending.CommitAt is { } commitAt)
                {
                    if (all || commitAt <= Now)
                    {
                        pending.CommitAt = null;
                        pending.Queued = true;
                        QueueUpload(pending);
                    }
                }
                else if (all && !StaysLocal(pending))
                {
                    // Changed, and its handles went with the drive: uploads now rather than at the next start
                    pending.Queued = true;
                    QueueUpload(pending);
                }
                break;
        }
    }

    /// <summary>
    /// The settle window of a renamed-away original that was staged passed. Unclaimed, the rename is undone as for an
    /// <see cref="AliasItem"/>: it goes back to the original's path. Claimed (a save put new content into the item), it goes; with
    /// changes of its own those are kept as a conflict copy next to the original.
    /// </summary>
    void SettleAside(PendingFile pending, AliasItem aside)
    {
        pending.Alias = null;
        var original = overlay.GetValueOrDefault(Key(pending.DriveId, aside.OriginalPath));
        if (!aside.Claimed && (original is null || original is GoneItem { DeleteOnExpiry: false } gone && gone.Item.Id == aside.Item.Id))
        {
            DropLocal(original);
            Move(pending, aside.OriginalPath);
            return;
        }
        if (!pending.Dirty)
        {
            DropLocal(pending);
            return;
        }
        Move(pending, JoinDrivePath(ParentOf(aside.OriginalPath), ConflictName(NameOf(aside.OriginalPath), 1)));
        pending.Base = null;
        pending.CommitAt ??= Now;
        JournalLater(pending);
        Log($"{aside.OriginalPath} was saved meanwhile; the changes made to it under {aside.Name} are kept as {pending.Name}");
    }

    /// <summary>
    /// Deletes an item whose settle window passed, without the write gate, at background priority (at unmount in the foreground) and
    /// DeleteConcurrency at a time across rounds, so saves don't wait for a bulk delete. Operations on its path wait only for this
    /// delete, which then goes ahead at once in the foreground. Called with the write gate held.
    /// </summary>
    void StartDelete(GoneItem gone, bool all)
    {
        var priority = new PriorityHolder(background: !all);
        var waited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transfer = new Transfer
        {
            MovesAlong = true,
            Waited = () =>
            {
                priority.Promote();
                waited.TrySetResult();
            },
        };
        Register(transfer, [gone.Key], []);
        Track(Task.Run(async () =>
        {
            RequestPriority.Use(priority);
            var slot = await TakeDeleteSlotAsync(waited.Task);
            (Exception? Failure, DriveItemInfo? Kept) outcome;
            try
            {
                outcome = await DeleteDueAsync(gone);
            }
            finally
            {
                if (slot)
                {
                    deleteSlots.Release();
                }
            }
            await writeGate.WaitAsync();
            try
            {
                Unregister(transfer);
                Deleted(gone, outcome.Failure, outcome.Kept);
            }
            finally
            {
                ReleaseGate();
                transfer.End();
            }
        }));
    }

    /// <summary>Waits for a delete slot, unless an operation waits for this delete first: then it goes at once. True when it took one.</summary>
    async Task<bool> TakeDeleteSlotAsync(Task waited)
    {
        var slot = deleteSlots.WaitAsync();
        if (await Task.WhenAny(slot, waited) == slot)
        {
            return true;
        }
        _ = slot.ContinueWith(_ => deleteSlots.Release(), TaskScheduler.Default); // taken later after all: given back
        return false;
    }

    /// <summary>
    /// A due delete, with If-Match: the version the user deleted. Changed since in its metadata only (412, same cTag), the item is
    /// deleted as it is now; with new content it is kept (Kept: the item now), so a colleague's newer version never goes by accident.
    /// </summary>
    async Task<(Exception? Failure, DriveItemInfo? Kept)> DeleteDueAsync(GoneItem gone)
    {
        try
        {
            try
            {
                await api.DeleteAsync(gone.DriveId, gone.Item.Id, gone.Item.ETag, CancellationToken.None);
            }
            catch (RemoteException e) when (e.Error == RemoteError.Conflict)
            {
                if (await api.GetItemByIdAsync(gone.DriveId, gone.Item.Id, CancellationToken.None) is not { } current)
                {
                    return (null, null);
                }
                if (current.CTag is null || current.CTag != gone.Item.CTag)
                {
                    return (new RemoteException(RemoteError.Conflict, $"412 {gone.Name} changed in Microsoft 365 after it was deleted here, so it is kept"), current);
                }
                Log($"{gone.DrivePath} changed in Microsoft 365 in its metadata only, deleting it");
                await api.DeleteAsync(gone.DriveId, current.Id, current.ETag, CancellationToken.None);
            }
            return (null, null);
        }
        catch (RemoteException e) when (e.Error == RemoteError.NotFound)
        {
            return (null, null);
        }
        catch (Exception e)
        {
            return (e, null);
        }
    }

    /// <summary>The outcome of a due delete. Called with the write gate held.</summary>
    void Deleted(GoneItem gone, Exception? failure, DriveItemInfo? kept)
    {
        if (failure is null)
        {
            DropLocal(gone);
            Forget(gone.DriveId, gone.DrivePath);
            Log($"deleted {gone.DrivePath} ({gone.Item.Id})");
        }
        else if (IsTransient(failure))
        {
            gone.Deadline = Now + RetryDelay(++gone.Failures);
            Log($"delete of {gone.DrivePath} failed, retrying in {RetryDelay(gone.Failures).TotalMinutes:0.#} min: {Short(failure)}");
        }
        else
        {
            // Refused (locked, no permission to delete) or changed meanwhile: the file shows again
            DropLocal(gone);
            if (kept is not null && kept.Name.Equals(gone.Name, StringComparison.OrdinalIgnoreCase))
            {
                Upsert(gone.DriveId, gone.DrivePath, kept);
            }
            Report(gone.DriveId, gone.DrivePath, ChangeKind.Added, false);
            Log($"error: delete of {gone.DrivePath} refused, it is back: {Short(failure)}");
            Notify(NoticeKind.NotDeleted, gone.DriveId, gone.DrivePath, Short(failure));
        }
    }

    /// <summary>Changed files that are not uploaded yet, for reporting at unmount.</summary>
    public IReadOnlyList<string> Unsaved() =>
        overlay.Values.OfType<PendingFile>().Where(p => p.Dirty && !StaysLocal(p)).Select(p => $"{p.DrivePath} ({p.StagingPath})").ToList();

    /// <summary>
    /// Changed files waiting to upload: a close (or a deferred commit) asked for the upload and it hasn't happened yet, failed ones
    /// included. Files still open for writing, refused by SharePoint or held under a temp name don't count.
    /// </summary>
    public int WaitingToUpload() =>
        overlay.Values.OfType<PendingFile>().Count(p => p.Dirty && p.Queued && !p.Rejected && !StaysLocal(p));

    /// <summary>A new file under a name Office's temp and lock files (or Explorer's folder files) use: never uploaded.</summary>
    static bool StaysLocal(PendingFile pending) => pending.Base is null && LocalOnlyNames.IsLocalOnly(pending.Name);

    /// <summary>A new file under a temp-like name (those included): uploads, if at all, only <see cref="TempUploadDelay"/> after its close.</summary>
    static bool TempLike(PendingFile pending) => pending.Base is null && LocalOnlyNames.IsTempLike(pending.Name);

    /// <summary>
    /// Renames staged content. To a temp name it stays local; a new file to a free name moves here at once (its upload goes on, see
    /// below); else this is the commit point of a save. Returns the file to flush, and whether to queue its upload, once the gate is let go.
    /// </summary>
    async Task<(PendingFile? Moved, bool Queue)> RenamePendingAsync(PendingFile pending, string to, DriveItemInfo? toItem, LocalState? toLocal, bool onServer, CancellationToken ct)
    {
        var driveId = pending.DriveId;
        var from = pending.DrivePath;
        if (pending.Progress is { } progress)
        {
            _ = AbandonAsync(pending, progress); // aimed at the old name; its fragment in flight ends on its own
        }
        pending.Rejected = false;
        if (LocalOnlyNames.IsTempLike(NameOf(to)) && !onServer)
        {
            // A save pattern's step, or a temp file: stays local for now
            DropLocal(toLocal);
            var stepsAside = pending.Base is not null && pending.Alias is null;
            if (stepsAside)
            {
                // Content of an item steps aside, as an original does: a save can claim the item, and deleting the temp name follows the alias rules
                pending.Alias = new AliasItem(driveId, to, pending.Base!, from, Now + options.SettleWindow);
            }
            Move(pending, to);
            if (stepsAside)
            {
                overlay[Key(driveId, from)] = new GoneItem(driveId, from, pending.Base!, Now + options.SettleWindow, deleteOnExpiry: false);
            }
            else if (pending.Base is null)
            {
                pending.TempSince = Now;
                if (pending.Queued || pending.RetryAt is not null || pending.CommitAt is not null)
                {
                    pending.CommitAt = Now + TempUploadDelay;
                    pending.RetryAt = null;
                    pending.Queued = false;
                }
            }
            return (null, false);
        }

        if (pending.Base is null && pending.Alias is null && toItem is null && options.UploadNewFilesInBackground)
        {
            // A new file to a free name (a browser's download at its end, a first save): moves here at once. An upload running goes on
            // and its item takes the name once it is up (FollowNameAsync); closed under a temp-like name, it uploads now.
            if (ParentOf(to) is { Length: > 0 } folder && !folder.Equals(ParentOf(from), StringComparison.OrdinalIgnoreCase)
                && await LookupAsync(driveId, folder, ct) is not { IsFolder: true })
            {
                throw new FsException(FsError.NotFound, $"{folder} isn't there");
            }
            DropLocal(toLocal);
            var uploading = transfers.GetValueOrDefault(pending.Key) is { } running && running.Upload == pending ? running : null;
            Move(pending, to);
            if (uploading is not null)
            {
                // Operations at the new name wait for it too; the old one stays held, the item is there until it moves
                Register(uploading, [pending.Key], []);
            }
            pending.TempSince = null;
            var queue = false;
            if (pending.Writers == 0 && uploading is null && pending.RetryAt is null && pending.Dirty)
            {
                pending.CommitAt = null;
                pending.Queued = true;
                queue = true;
            }
            Log($"renamed {from} to {to}, {(uploading is not null ? "its upload goes on" : "not uploaded yet")}");
            return (pending, queue);
        }

        // Commit point of a save: content goes into the item that owns the destination, else the file's own item, else a new file.
        // Failures reach the application, which keeps its temp file and can retry.
        var writes = pending.Writes;
        var originalBase = pending.Base;
        var aside = pending.Alias;
        var into = toItem ?? originalBase;
        var earlier = pending.Unconfirmed is { } unconfirmed && (into is null
            ? unconfirmed.Target.ItemId is null && unconfirmed.Target.Name == NameOf(to) && unconfirmed.Target.ParentPath == ParentOf(to)
            : unconfirmed.Target.ItemId == into.Id) ? unconfirmed : null;
        pending.Unconfirmed = null;
        UploadTarget? sent = null;
        DriveItemInfo? uploaded = null;
        DriveItemInfo item;
        try
        {
            item = await TransferAsync(async () =>
            {
                if (earlier is not null && await LandedEarlierAsync(pending, earlier, ct) is { } landed)
                {
                    Log($"{to}: the last save got no answer, but it went through");
                    if (into is null)
                    {
                        return landed;
                    }
                    uploaded = landed;
                }
                else if (into is null)
                {
                    sent = await NewTargetAsync(driveId, ParentOf(to), NameOf(to), ct);
                    return await api.UploadAsync(driveId, sent, pending.StagingPath, ct);
                }
                else
                {
                    try
                    {
                        sent = UploadTarget.Existing(into);
                        uploaded = await UploadIntoAsync(driveId, into, pending.StagingPath, ct);
                    }
                    catch (RemoteException e) when (e.Error is RemoteError.NotFound or RemoteError.Gone)
                    {
                        // Deleted in Microsoft 365 meanwhile: the content becomes a new file under the name
                        into = null;
                        sent = await NewTargetAsync(driveId, ParentOf(to), NameOf(to), ct);
                        return await api.UploadAsync(driveId, sent, pending.StagingPath, ct);
                    }
                }
                return toItem is null ? await api.MoveAsync(driveId, uploaded!.Id, await FolderIdAsync(driveId, ParentOf(to), ct), NameOf(to), ct) : uploaded!;
            }, pending.Key, Key(driveId, to));
        }
        catch when (uploaded is not null)
        {
            // The content is in the file's own item, the move failed: later uploads go into the item as it is now
            pending.Base = uploaded;
            Upsert(driveId, from, uploaded);
            JournalLater(pending);
            throw;
        }
        catch (Exception e) when (sent is not null && !ct.IsCancellationRequested && MayHaveLanded(e) && pending.Writes == writes)
        {
            // No answer: it may have gone through, which the next attempt looks up before it sends the file again
            pending.Unconfirmed = new UnconfirmedUpload(sent, writes);
            throw;
        }
        if (toItem is null && originalBase is not null)
        {
            Forget(driveId, from);
            if (aside is not null)
            {
                Forget(driveId, aside.OriginalPath); // the renamed-away original's item moved to the new name
            }
        }

        DropLocal(toLocal);
        pending.Writing.EnterWriteLock();
        try
        {
            if (pending.Writes != writes)
            {
                // Written to while it uploaded: stays staged under the new name and uploads again
                overlay.TryRemove(pending.Key, out _);
                pending.DrivePath = to;
                pending.Base = item;
                pending.Alias = null;
                overlay[pending.Key] = pending;
                Claimed(driveId, item);
                Saved(item, into);
                Upsert(driveId, to, item);
                JournalLater(pending);
                pending.CommitAt ??= Now + options.PagingCommitDelay;
            }
            else
            {
                Uploaded(pending, item, to, into);
            }
        }
        finally
        {
            pending.Writing.ExitWriteLock();
        }
        if (toItem is not null && originalBase is { } replaced && replaced.Id != toItem.Id)
        {
            if (aside is not null)
            {
                DeleteAlias(aside);
            }
            else
            {
                overlay[Key(driveId, from)] = new GoneItem(driveId, from, replaced, Now + options.SettleWindow, deleteOnExpiry: true);
            }
        }
        Log($"saved {to} into {item.Id} (from {NameOf(from)})");
        return (null, false);
    }

    async Task RenameAliasAsync(AliasItem alias, string to, DriveItemInfo? toItem, LocalState? toLocal, bool onServer, CancellationToken ct)
    {
        var driveId = alias.DriveId;
        if (to.Equals(alias.OriginalPath, StringComparison.OrdinalIgnoreCase))
        {
            // Renamed back: the save didn't happen
            DropLocal(alias);
            DropLocal(toLocal);
            return;
        }
        if (LocalOnlyNames.IsTempLike(NameOf(to)) && !onServer)
        {
            DropLocal(toLocal);
            Move(alias, to);
            return;
        }
        // A save claimed the item, or new content staged into it will (a file made where the original was takes over its item)
        if (alias.Claimed || overlay.Values.Any(l => l is PendingFile { Base: { } staged } && l.DriveId == driveId && staged.Id == alias.Item.Id))
        {
            await BackupAsync(alias, to, onServer ? toItem : null, toLocal, ct);
            return;
        }
        var originalKey = Key(driveId, alias.OriginalPath);
        if (onServer && toItem is not null && toItem.Id != alias.Item.Id)
        {
            // Replacing a file, as in RenameAsync: the alias's content goes into the destination item, which keeps its identity
            var file = NewStagingFile();
            DriveItemInfo updated;
            try
            {
                updated = await TransferAsync(async () =>
                {
                    await content.CopyToAsync(driveId, alias.Item.Id, alias.Item.CTag ?? alias.Item.ETag, ServedSize(driveId, alias.Item), file, ct,
                        current => ContentChanged(driveId, current));
                    return await UploadIntoAsync(driveId, toItem, file, ct);
                }, alias.Key, Key(driveId, to), originalKey);
            }
            catch
            {
                TryDelete(file);
                throw;
            }
            DropLocal(alias);
            DropLocal(toLocal);
            Claimed(driveId, updated);
            Saved(updated, toItem);
            Upsert(driveId, to, updated);
            // Its own item goes, unless a save went into it meanwhile
            if (!alias.Claimed && (overlay.GetValueOrDefault(originalKey) is not { } atOriginal || atOriginal is GoneItem gone && gone.Item.Id == alias.Item.Id))
            {
                overlay[originalKey] = new GoneItem(driveId, alias.OriginalPath, alias.Item, Now + options.SettleWindow, deleteOnExpiry: true);
            }
            AdoptLater(driveId, updated, file, Key(driveId, to));
            Log($"replaced {to} with {alias.DrivePath}");
            return;
        }

        var moved = await TransferAsync(async () => await api.MoveAsync(driveId, alias.Item.Id, await FolderIdAsync(driveId, ParentOf(to), ct), NameOf(to), ct),
            alias.Key, Key(driveId, to), originalKey);
        DropLocal(alias);
        if (overlay.GetValueOrDefault(originalKey) is GoneItem original && original.Item.Id == alias.Item.Id)
        {
            DropLocal(original);
        }
        DropLocal(toLocal);
        Forget(driveId, alias.OriginalPath);
        Upsert(driveId, to, moved);
    }

    /// <summary>
    /// A claimed alias renamed to a real name (Word's backup copy): the item holds the new content, so the alias stands for the version
    /// before the save, and that becomes a file of its own. The item itself never moves, it would take the save along. Refused when
    /// that version isn't cached here.
    /// </summary>
    async Task BackupAsync(AliasItem alias, string to, DriveItemInfo? into, LocalState? toLocal, CancellationToken ct)
    {
        var driveId = alias.DriveId;
        var tag = alias.Item.CTag ?? alias.Item.ETag;
        var notCached = $"{to}: the version of {NameOf(alias.OriginalPath)} before the save isn't cached here, so it can't be kept as a copy";
        if (!content.Has(driveId, alias.Item.Id, tag))
        {
            throw new FsException(FsError.AccessDenied, notCached);
        }
        var file = NewStagingFile();
        DriveItemInfo backup;
        try
        {
            backup = await TransferAsync(async () =>
            {
                await content.CopyToAsync(driveId, alias.Item.Id, tag, ServedSize(driveId, alias.Item), file, ct);
                return into is null
                    ? await api.UploadAsync(driveId, await NewTargetAsync(driveId, ParentOf(to), NameOf(to), ct), file, ct)
                    : await UploadIntoAsync(driveId, into, file, ct);
            }, alias.Key, Key(driveId, to));
        }
        catch (ContentChangedException)
        {
            // Only partly here (a read was downloading it), and the item holds the save by now
            TryDelete(file);
            throw new FsException(FsError.AccessDenied, notCached);
        }
        catch
        {
            TryDelete(file);
            throw;
        }
        DropLocal(alias);
        DropLocal(toLocal);
        Upsert(driveId, to, backup);
        AdoptLater(driveId, backup, file, Key(driveId, to));
        Log($"kept the version of {alias.OriginalPath} before the save as {to} ({backup.Id})");
    }

    /// <summary>
    /// What became of an upload: the item the content went into, at which path and how, or the failure. Unconfirmed: the target of an
    /// upload that failed without an answer and couldn't be looked up, checked before the next attempt.
    /// </summary>
    sealed record UploadOutcome(DriveItemInfo? Item, string DrivePath, UploadKind Kind, Exception? Cause, UploadTarget? Unconfirmed = null);

    enum UploadKind { Saved, AsNew, ConflictCopy, Recovered, Failed }

    /// <summary>
    /// Uploads a pending file. Called with the write gate held; lets it go during the transfer, so other files go on meanwhile, and
    /// holds it again before applying the outcome. Operations on this file wait for it (<see cref="SettleAsync"/>), except a rename of
    /// a new file, which it follows. A failed upload doesn't throw: the file stays staged and is retried with back-off, unless
    /// SharePoint refuses it. The caller flushes the file once the gate is let go.
    /// </summary>
    async Task UploadPendingAsync(PendingFile pending, CancellationToken ct)
    {
        if (StaysLocal(pending))
        {
            // Renamed to a temp name after its close: stays local
            pending.CommitAt = pending.RetryAt = null;
            pending.Queued = false;
            return;
        }
        if (TempLike(pending) && (pending.TempSince is not { } since || Now < since + TempUploadDelay))
        {
            // Under a temp-like name since lately: uploads when it is still there later
            pending.TempSince ??= Now;
            pending.CommitAt = pending.TempSince + TempUploadDelay;
            pending.RetryAt = null;
            pending.Queued = false;
            return;
        }
        if (pending.Alias is { } aside)
        {
            if (aside.Deadline > Now)
            {
                // A renamed-away original: a save may claim its item any moment, so it waits for that to settle
                pending.CommitAt ??= aside.Deadline;
                return;
            }
            SettleAside(pending, aside);
            if (!ReferenceEquals(overlay.GetValueOrDefault(pending.Key), pending))
            {
                return;
            }
        }
        var replaced = pending.Base;
        var writes = pending.Writes;
        var isNew = pending.Base is null;
        UploadOutcome? outcome = null;
        if (pending.Unconfirmed is { } earlier)
        {
            // The last attempt got no answer: it may have gone through, and sending it again would end in a conflict copy
            pending.Unconfirmed = null;
            try
            {
                string[] held = pending.ServerPath is { } at && !at.Equals(pending.DrivePath, StringComparison.Ordinal)
                    ? [pending.Key, Key(pending.DriveId, at)] : [pending.Key];
                if (await TransferAsync(() => LandedEarlierAsync(pending, earlier, ct), new Transfer { Upload = pending }, held) is { } landed)
                {
                    Log($"{pending.DrivePath}: the last upload got no answer, but it went through");
                    outcome = new(landed, pending.DrivePath, UploadKind.Saved, null);
                }
            }
            catch (Exception e)
            {
                outcome = new(null, pending.DrivePath, UploadKind.Failed, e, earlier.Target);
            }
        }
        if (outcome is null)
        {
            if (isNew)
            {
                pending.ServerPath = pending.DrivePath;
            }
            var target = TargetOf(pending);
            outcome = await TransferAsync(() => SendAsync(pending, target, writes, ct), new Transfer { Upload = pending }, [pending.Key]);
        }
        if (outcome.Kind is UploadKind.Saved or UploadKind.AsNew && ReferenceEquals(overlay.GetValueOrDefault(pending.Key), pending))
        {
            outcome = await FollowNameAsync(pending, outcome, ct);
        }

        if (outcome.Kind == UploadKind.Failed)
        {
            var cause = outcome.Cause!;
            if (outcome.Unconfirmed is { } sent && pending.Writes == writes)
            {
                pending.Unconfirmed = new UnconfirmedUpload(sent, writes);
            }
            if (cause is RemoteException { Error: RemoteError.Rejected })
            {
                // Retrying can't help: stays here, and in "Not saved", until it is saved or renamed again
                pending.Rejected = true;
                pending.RetryAt = null;
                Log($"error: upload of {pending.DrivePath} refused by Microsoft 365, it stays on this PC until it is renamed or saved again: {Short(cause)}");
            }
            else
            {
                pending.RetryAt = Now + RetryDelay(++pending.Failures);
                Log($"upload of {pending.DrivePath} failed, retrying in {RetryDelay(pending.Failures).TotalMinutes:0.#} min: {Short(cause)}");
            }
            if (!pending.Waiting)
            {
                pending.Waiting = true;
                Notify(NoticeKind.UploadWaiting, pending.DriveId, pending.DrivePath, Short(cause));
            }
            return;
        }

        var item = outcome.Item!;
        var original = pending.DrivePath;
        pending.Writing.EnterWriteLock();
        try
        {
            if (outcome.Kind is UploadKind.ConflictCopy or UploadKind.Recovered)
            {
                // The content lives on in the copy; the original shows the server's version again
                Invalidate(pending.DriveId, original);
            }
            if (pending.Writes != writes)
            {
                // Written to while it uploaded: the item has the earlier content, the rest follows with the next commit
                if (outcome.DrivePath != original)
                {
                    overlay.TryRemove(pending.Key, out _);
                    pending.DrivePath = outcome.DrivePath;
                    overlay[pending.Key] = pending;
                }
                pending.Base = item;
                pending.ServerPath = null;
                pending.RecoverAsCopy = false;
                Saved(item, replaced);
                Upsert(pending.DriveId, outcome.DrivePath, item);
                JournalLater(pending);
                pending.CommitAt ??= Now + options.PagingCommitDelay;
                Log($"saved {outcome.DrivePath} into {item.Id}; it changed meanwhile, uploading again");
            }
            else
            {
                Uploaded(pending, item, outcome.DrivePath, outcome.Kind == UploadKind.Saved ? replaced : null);
                Log($"saved {outcome.DrivePath} into {item.Id}{(isNew || outcome.Kind != UploadKind.Saved ? " (new)" : "")}");
            }
        }
        finally
        {
            pending.Writing.ExitWriteLock();
        }

        switch (outcome.Kind)
        {
            case UploadKind.ConflictCopy:
                var why = outcome.Cause switch
                {
                    RemoteException { Error: RemoteError.Locked } => "locked",
                    RemoteException { Error: RemoteError.NotFound or RemoteError.Gone } => "deleted",
                    _ => "changed",
                };
                var other = why == "changed" ? (outcome.Cause as RemoteException)?.Current : null;
                Log($"{original} is {why} on the server: saved as {item.Name}"); // who saved it shows only in the notification
                ConflictCopyMade(pending.DriveId, item, other);
                Notify(NoticeKind.ConflictCopy, pending.DriveId, original, item.Name, other);
                break;
            case UploadKind.Recovered:
                Log($"{original} was still being written when the PC or the agent stopped: saved next to it as {item.Name}, the file itself is unchanged");
                Notify(NoticeKind.ConflictCopy, pending.DriveId, original, item.Name);
                break;
            case UploadKind.AsNew:
                Log($"{original} was deleted on the server while it was changed here: saved as a new file");
                goto default;
            default:
                if (pending.Waiting)
                {
                    Notify(NoticeKind.UploadDone, pending.DriveId, original, null);
                }
                break;
        }
    }

    /// <summary>
    /// The upload of a new file went through while the file was renamed or moved here (RenamePendingAsync): its item follows on the
    /// server. A name taken there meanwhile gets a conflict copy's name; another failure keeps the file staged into the item, to try
    /// again. Called with the write gate held; the outcome is then at the file's path.
    /// </summary>
    async Task<UploadOutcome> FollowNameAsync(PendingFile pending, UploadOutcome outcome, CancellationToken ct)
    {
        var item = outcome.Item!;
        for (var attempt = 1; pending.ServerPath is { } at && !at.Equals(pending.DrivePath, StringComparison.Ordinal);)
        {
            if (Blocking([pending.Key], t => t.Upload == pending) is not null)
            {
                await SettleExceptAsync(t => t.Upload == pending, pending.Key); // a move of a folder above it ends first
                continue;
            }
            var to = pending.DrivePath;
            try
            {
                // The name it has on the server stays held until it moves: a new file there would collide with it
                item = await TransferAsync(async () => await api.MoveAsync(pending.DriveId, item.Id, await FolderIdAsync(pending.DriveId, ParentOf(to), ct), NameOf(to), ct),
                    new Transfer { Upload = pending }, [pending.Key, Key(pending.DriveId, at)]);
                pending.ServerPath = to;
                Forget(pending.DriveId, at);
            }
            catch (RemoteException e) when (e.Error == RemoteError.Conflict && attempt < 10)
            {
                // The name was taken on the server meanwhile: the file keeps a conflict copy's name next to it
                var copy = JoinDrivePath(ParentOf(to), ConflictName(NameOf(to), attempt++));
                if (pending.DrivePath == to && !overlay.ContainsKey(Key(pending.DriveId, copy)))
                {
                    Move(pending, copy);
                    Log($"{to} was taken on the server meanwhile: saved as {NameOf(copy)}");
                    Notify(NoticeKind.ConflictCopy, pending.DriveId, to, NameOf(copy));
                }
            }
            catch (Exception e)
            {
                // The content is up under the earlier name: the file stays staged into that item, and the retry finds the content there
                // (unless written meanwhile) and sends only the name
                pending.Base = item;
                pending.Unconfirmed = new UnconfirmedUpload(UploadTarget.Existing(item), pending.Writes);
                Upsert(pending.DriveId, at, item);
                JournalLater(pending);
                return new(null, pending.DrivePath, UploadKind.Failed, e);
            }
        }
        pending.ServerPath = null;
        return outcome with { Item = item, DrivePath = pending.DrivePath };
    }

    /// <summary>
    /// The transfer of <see cref="UploadPendingAsync"/>, without the write gate: into the item, else as a new file under its name when
    /// the item was deleted on the server, else as a conflict copy next to it. Content recovered without a flush goes only into a
    /// "(recovered)" copy. A failure without an answer is looked up at once: it may have gone through.
    /// </summary>
    async Task<UploadOutcome> SendAsync(PendingFile pending, UploadTarget target, int writes, CancellationToken ct)
    {
        UploadTarget? sent = null;
        try
        {
            if (pending.Recovered)
            {
                var done = await AlreadyUploadedAsync(pending, ct);
                pending.Recovered = false;
                if (done is not null)
                {
                    Log($"{pending.DrivePath} was uploaded before the last stop");
                    return new(done, pending.DrivePath, UploadKind.Saved, null);
                }
            }
            if (pending.RecoverAsCopy)
            {
                return await CopyAsync(pending, RecoveredName, UploadKind.Recovered, null, ct);
            }
            try
            {
                sent = pending.Progress is not null ? target : null;
                if (await FinishProgressiveAsync(pending, ct) is { } streamed)
                {
                    return new(streamed, pending.DrivePath, UploadKind.Saved, null);
                }
                if (pending.Base is { } baseItem)
                {
                    sent = target;
                    return new(await UploadIntoAsync(pending.DriveId, baseItem, pending.StagingPath, ct), pending.DrivePath, UploadKind.Saved, null);
                }
                sent = await NewTargetAsync(pending.DriveId, target.ParentPath!, target.Name!, ct);
                return new(await api.UploadAsync(pending.DriveId, sent, pending.StagingPath, ct), pending.DrivePath, UploadKind.Saved, null);
            }
            catch (RemoteException e) when (target.ItemId is not null && e.Error is RemoteError.NotFound or RemoteError.Gone)
            {
                try
                {
                    sent = await NewTargetAsync(pending.DriveId, ParentOf(pending.DrivePath), pending.Name, ct);
                    var created = await api.UploadAsync(pending.DriveId, sent, pending.StagingPath, ct);
                    return new(created, pending.DrivePath, UploadKind.AsNew, e);
                }
                catch (RemoteException again) when (again.Error == RemoteError.Conflict)
                {
                    sent = null;
                    return await CopyAsync(pending, ConflictName, UploadKind.ConflictCopy, e, ct);
                }
            }
            catch (RemoteException e) when (e.Error is RemoteError.Conflict or RemoteError.Locked)
            {
                sent = null;
                return await CopyAsync(pending, ConflictName, UploadKind.ConflictCopy, e, ct);
            }
        }
        catch (Exception e) when (sent is not null && !ct.IsCancellationRequested && MayHaveLanded(e) && pending.Writes == writes)
        {
            try
            {
                if (await LandedAsync(pending, sent, ct) is { } landed)
                {
                    Log($"{pending.DrivePath}: the upload got no answer, but it went through");
                    return new(landed, pending.DrivePath, UploadKind.Saved, null);
                }
                return new(null, pending.DrivePath, UploadKind.Failed, e);
            }
            catch (Exception)
            {
                return new(null, pending.DrivePath, UploadKind.Failed, e, sent);
            }
        }
        catch (Exception e)
        {
            return new(null, pending.DrivePath, UploadKind.Failed, e);
        }
    }

    /// <summary>A failure that doesn't tell whether a write reached Microsoft 365: no answer (network, timeout) or a server error.</summary>
    static bool MayHaveLanded(Exception e) =>
        e is HttpRequestException or IOException or TimeoutException or OperationCanceledException || e is RemoteException { Error: RemoteError.Unavailable };

    /// <summary>
    /// An earlier upload of this content got no answer: the item it reached when it went through after all, null when it didn't or the
    /// content changed since (then it can't be told). Throws when Microsoft 365 can't be asked.
    /// </summary>
    async Task<DriveItemInfo?> LandedEarlierAsync(PendingFile pending, UnconfirmedUpload earlier, CancellationToken ct) =>
        earlier.Writes == pending.Writes ? await LandedAsync(pending, earlier.Target, ct) : null;

    /// <summary>
    /// After an upload of the staged content that ended without an answer: the item at its target when it holds this content (same size
    /// and quickXorHash), null when it doesn't or there is no hash to tell by. Throws when Microsoft 365 can't be asked.
    /// </summary>
    async Task<DriveItemInfo?> LandedAsync(PendingFile pending, UploadTarget target, CancellationToken ct)
    {
        if (await api.GetUploadedAsync(pending.DriveId, target, ct) is not { QuickXorHash: { } hash, Item: { IsFolder: false } item }
            || item.Size != new FileInfo(pending.StagingPath).Length)
        {
            return null;
        }
        return hash == QuickXorHash.OfFile(pending.StagingPath) ? item : null;
    }

    /// <summary>A new file's target, with its folder by item ID (but the library's top): it lands there even when the folder moves meanwhile.</summary>
    async Task<UploadTarget> NewTargetAsync(string driveId, string parentPath, string name, CancellationToken ct)
    {
        var target = UploadTarget.New(parentPath, name);
        if (parentPath.Length == 0)
        {
            return target;
        }
        return target with
        {
            ParentId = listings.TryGetValue(Key(driveId, parentPath), out var listing) && listing.FolderId is { } id ? id : await FolderIdAsync(driveId, parentPath, ct),
        };
    }

    /// <summary>
    /// Uploads into an existing item with If-Match. A 412 while the item still has the same content (its cTag: only a column, a label or
    /// the Document ID changed) is tried once more with the item's eTag now, so a metadata change doesn't end a save in a conflict copy.
    /// </summary>
    async Task<DriveItemInfo> UploadIntoAsync(string driveId, DriveItemInfo into, string file, CancellationToken ct)
    {
        if (StandInETag(into))
        {
            // A version read whose eTag isn't known (AsRead): the item must still hold that content
            var current = await api.GetItemByIdAsync(driveId, into.Id, ct) ?? throw new RemoteException(RemoteError.NotFound, $"{into.Name} was deleted in Microsoft 365");
            into = current.CTag == into.CTag ? current : throw new RemoteException(RemoteError.Conflict, $"412 {into.Name} changed in Microsoft 365 since it was read") { Current = current };
        }
        try
        {
            return await api.UploadAsync(driveId, UploadTarget.Existing(into), file, ct);
        }
        catch (RemoteException e) when (e.Error == RemoteError.Conflict && into.CTag is not null)
        {
            if (await api.GetItemByIdAsync(driveId, into.Id, ct) is not { } current || current.ETag == into.ETag)
            {
                throw;
            }
            if (current.CTag != into.CTag)
            {
                throw new RemoteException(e.Error, e.Message) { Current = current }; // who saved it, for the conflict copy's notice
            }
            Log($"{into.Name} changed in Microsoft 365 in its metadata only, saving into it");
            return await api.UploadAsync(driveId, UploadTarget.Existing(current), file, ct);
        }
    }

    /// <summary>Uploads the staged content next to the file under a copy's name; under another one when that is taken too.</summary>
    async Task<UploadOutcome> CopyAsync(PendingFile pending, Func<string, int, string> name, UploadKind kind, Exception? cause, CancellationToken ct)
    {
        var parent = ParentOf(pending.DrivePath);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var copy = await api.UploadAsync(pending.DriveId, await NewTargetAsync(pending.DriveId, parent, name(pending.Name, attempt), ct), pending.StagingPath, ct);
                return new(copy, JoinDrivePath(parent, copy.Name), kind, cause);
            }
            catch (RemoteException e) when (e.Error == RemoteError.Conflict && attempt < 10)
            {
            }
        }
    }

    /// <summary>
    /// For a file recovered after a crash: the item at its path when it has the staged content already (the upload went through before
    /// the journal was removed), else null. Told by quickXorHash; downloaded and compared only when Microsoft 365 has no hash for it.
    /// </summary>
    async Task<DriveItemInfo?> AlreadyUploadedAsync(PendingFile pending, CancellationToken ct)
    {
        if (await api.GetUploadedAsync(pending.DriveId, UploadTarget.New(ParentOf(pending.DrivePath), pending.Name), ct) is not { Item: var current } found
            || current.IsFolder || current.Size != new FileInfo(pending.StagingPath).Length
            || (pending.Base is { } baseItem && (current.Id != baseItem.Id || current.ETag == baseItem.ETag)))
        {
            return null;
        }
        if (found.QuickXorHash is { } hash)
        {
            return hash == QuickXorHash.OfFile(pending.StagingPath) ? current : null;
        }
        var copy = NewStagingFile();
        try
        {
            await content.CopyToAsync(pending.DriveId, current.Id, current.CTag ?? current.ETag, current.Size, copy, ct);
            return SameContent(copy, pending.StagingPath) ? current : null;
        }
        catch (ContentChangedException)
        {
            return null;
        }
        finally
        {
            TryDelete(copy);
        }
    }

    static bool SameContent(string a, string b)
    {
        using var first = File.OpenHandle(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var second = File.OpenHandle(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (RandomAccess.GetLength(first) != RandomAccess.GetLength(second))
        {
            return false;
        }
        var left = new byte[81920];
        var right = new byte[left.Length];
        for (long offset = 0; ; offset += left.Length)
        {
            var read = RandomAccess.Read(first, left, offset);
            if (read == 0)
            {
                return true;
            }
            if (RandomAccess.Read(second, right.AsSpan(0, read), offset) != read || !left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read)))
            {
                return false;
            }
        }
    }

    /// <summary>Waits for the background uploads, deletes and caching of uploads queued or running now.</summary>
    public async Task WaitForUploadsAsync()
    {
        while (RunningUploads() is { Length: > 0 } running)
        {
            await Task.WhenAll(running);
        }
    }

    /// <summary>
    /// The pending content is now <paramref name="item"/> at <paramref name="drivePath"/>, replacing <paramref name="replaced"/> when that
    /// is the same item. Called with its write lock and the write gate held; the staging file becomes cached content after the gate.
    /// </summary>
    void Uploaded(PendingFile pending, DriveItemInfo item, string drivePath, DriveItemInfo? replaced)
    {
        recentUploads[item.Id] = Now;
        Saved(item, replaced);
        pending.Retire(item, drivePath);
        Upsert(pending.DriveId, drivePath, item); // before the pending file goes, so the new file is never briefly not found
        overlay.TryRemove(new KeyValuePair<string, LocalState>(pending.Key, pending));
        if (overlay.GetValueOrDefault(Key(pending.DriveId, drivePath)) is GoneItem gone)
        {
            DropLocal(gone);
        }
        Claimed(pending.DriveId, item);
        if (!item.Name.Equals(NameOf(drivePath), StringComparison.OrdinalIgnoreCase))
        {
            // Content under a temp name went into its item, which keeps its own name: nothing is at the temp name, so deleting it can't reach the item
            Forget(pending.DriveId, drivePath);
        }
        Unjournal(pending);
        AdoptLater(pending.DriveId, item, pending.StagingPath, Key(pending.DriveId, drivePath));
    }

    /// <summary>
    /// The staging file of uploaded content becomes the item's cached content without the write gate (encrypting a large file takes
    /// seconds). Reads of that version use the staging file until then (<see cref="Follow"/>); staging the file again waits for it, as
    /// for a transfer. Content SharePoint changed on upload (Office files) isn't kept: reads go to Microsoft 365 at once. Called with the
    /// write gate held.
    /// </summary>
    void AdoptLater(string driveId, DriveItemInfo item, string staging, string key)
    {
        var version = AdoptKey(driveId, item.Id, item.CTag ?? item.ETag);
        long length;
        try
        {
            length = new FileInfo(staging).Length;
        }
        catch (IOException)
        {
            length = -1;
        }
        // The cache takes a second name of the file, so a read handed the staging path (Follow) just before it is done still opens it
        var source = staging;
        if (length == item.Size)
        {
            adopting[version] = staging;
            if (TryLink(staging, staging + ".adopt"))
            {
                source = staging + ".adopt";
            }
        }
        var transfer = new Transfer { MovesAlong = true };
        Register(transfer, [key], []);
        Track(Task.Run(async () =>
        {
            try
            {
                content.Adopt(driveId, item, source);
            }
            finally
            {
                adopting.TryRemove(new KeyValuePair<string, string>(version, staging));
                await writeGate.WaitAsync();
                try
                {
                    Unregister(transfer);
                }
                finally
                {
                    ReleaseGate();
                    transfer.End();
                }
            }
            if (source != staging)
            {
                await Task.Delay(StagingReadGrace);
                TryDelete(staging);
            }
        }));
    }

    /// <summary>How long a staging file stays after its content was cached, for reads that were handed its path just before.</summary>
    static readonly TimeSpan StagingReadGrace = TimeSpan.FromMilliseconds(250);

    /// <summary>A second name for a file (a hard link); false where the file system has none, and the file is then cached under its own.</summary>
    static bool TryLink(string existing, string link) => OperatingSystem.IsWindows() && CreateHardLink(link, existing, IntPtr.Zero);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    /// <summary>New content went into the item: it is no longer hidden or waiting to be restored anywhere.</summary>
    void Claimed(string driveId, DriveItemInfo item)
    {
        foreach (var local in overlay.Values.Where(l => l.DriveId == driveId).ToList())
        {
            if (local is GoneItem gone && gone.Item.Id == item.Id)
            {
                DropLocal(gone);
            }
            else if (local is AliasItem alias && alias.Item.Id == item.Id)
            {
                alias.Claimed = true;
            }
            else if (local is PendingFile { Alias: { } staged } && staged.Item.Id == item.Id)
            {
                staged.Claimed = true;
            }
        }
    }

    /// <summary>A renamed-away original is deleted: unless a save claimed it, the original goes too.</summary>
    void DeleteAlias(AliasItem alias)
    {
        if (!alias.Claimed && overlay.GetValueOrDefault(Key(alias.DriveId, alias.OriginalPath)) is GoneItem original && original.Item.Id == alias.Item.Id)
        {
            original.DeleteOnExpiry = true;
            original.Deadline = Now + options.SettleWindow;
        }
    }

    void DropLocal(LocalState? local)
    {
        if (local is null)
        {
            return;
        }
        overlay.TryRemove(new KeyValuePair<string, LocalState>(local.Key, local));
        if (local is PendingFile pending)
        {
            pending.Writing.EnterWriteLock();
            try
            {
                pending.Discard();
            }
            finally
            {
                pending.Writing.ExitWriteLock();
            }
            if (pending.Progress is { } progress)
            {
                _ = AbandonAsync(pending, progress);
            }
            TryDelete(pending.StagingPath);
            Unjournal(pending);
        }
    }

    async Task<WriteTarget> WritableAsync(string path, CancellationToken ct)
    {
        var resolved = ns.Resolve(path);
        if (resolved?.Drive is null || resolved.DrivePath.Length == 0)
        {
            throw new FsException(FsError.AccessDenied, $"{path} is part of the fixed tree");
        }
        if (resolved.Drive.ReadOnly)
        {
            throw new FsException(FsError.ReadOnly, $"{path} is in a read-only library");
        }
        if (options.DatabaseFiles != DatabaseFilePolicy.Allow && DatabaseFiles.IsDatabase(NameOf(resolved.DrivePath)))
        {
            throw new FsException(FsError.ReadOnly, $"{path} is a multi-user database file, which policy keeps read-only");
        }
        return new WriteTarget(resolved, resolved.Drive, await resolved.Drive.GetDriveIdAsync(ct));
    }

    async Task<string> FolderIdAsync(string driveId, string folderPath, CancellationToken ct)
    {
        if (folderPath.Length > 0)
        {
            return (await LookupAsync(driveId, folderPath, ct, refreshParent: false))?.Id ?? throw new FsException(FsError.NotFound, folderPath);
        }
        var key = Key(driveId, "");
        if (lookups.TryGetValue(key, out var known) && known.Item is { } root)
        {
            return root.Id;
        }
        root = await api.GetItemAsync(driveId, "", ct) ?? throw new FsException(FsError.NotFound, "drive root");
        lookups[key] = (root, Now);
        return root.Id;
    }

    string NewStagingFile()
    {
        Directory.CreateDirectory(options.StagingDirectory);
        var path = Path.Combine(options.StagingDirectory, $"{Guid.NewGuid():N}.staging");
        File.Create(path).Dispose();
        return path;
    }

    string ConflictName(string name, int attempt) => CopyName(name, "conflict", attempt);

    string RecoveredName(string name, int attempt) => CopyName(name, "recovered", attempt);

    string CopyName(string name, string what, int attempt) =>
        $"{Path.GetFileNameWithoutExtension(name)} ({what} {Now.ToLocalTime():yyyy-MM-dd HHmmss}{(attempt > 1 ? $" {attempt}" : "")}){Path.GetExtension(name)}";

    /// <summary>Forces a staged file to disk, so a close acknowledged before its upload survives a power loss. False when that failed.</summary>
    bool FlushToDisk(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            file.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"error: flushing {path}: {e.Message}");
            return false;
        }
    }

    /// <summary>Retries back off: RetryInterval, then twice as long each time, up to 15 minutes.</summary>
    TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(15).Ticks, options.RetryInterval.Ticks << Math.Clamp(failures - 1, 0, 10)));

    /// <summary>A <see cref="RemoteError.ReadOnly"/> in words, for the log and notices.</summary>
    const string ReadOnlyCause = "SharePoint has the library read-only for now (maintenance or a site move)";

    /// <summary>What went wrong, in words a user or admin can act on.</summary>
    static string Short(Exception e) => e switch
    {
        RemoteException { Error: RemoteError.ReadOnly } => ReadOnlyCause,
        RemoteException { Error: RemoteError.Rejected } => $"Microsoft 365 refuses the name or the request ({e.Message})",
        _ => e.Message,
    };

    /// <summary>
    /// Worth retrying: network and sign-in errors (the token source's own exceptions, timeouts) and service states that pass; a
    /// read-only library only with <paramref name="readOnlyCounts"/>.
    /// </summary>
    static bool IsTransient(Exception e, bool readOnlyCounts = true) =>
        e is RemoteException remote
            ? remote.Error is RemoteError.Throttled or RemoteError.Unavailable || readOnlyCounts && remote.Error == RemoteError.ReadOnly
            : e is not FsException;

    static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    void Log(string message) => options.Log?.Invoke(message);

    void Notify(NoticeKind kind, string driveId, string drivePath, string? detail, DriveItemInfo? other = null) =>
        options.Notice?.Invoke(new EngineNotice(kind, NameOf(drivePath), VolumePath(driveId, drivePath), detail, other?.ModifiedBy, other?.Modified));

    /// <summary>Where a drive path shows on the volume; null when its library isn't mounted.</summary>
    string? VolumePath(string driveId, string drivePath) =>
        ns.VolumePathOf(driveId) is { } top ? Join(top, drivePath.Replace('/', '\\')) : null;
}
