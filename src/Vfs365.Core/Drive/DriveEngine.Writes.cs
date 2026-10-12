using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vfs365.Core.Drive;

/// <summary>
/// Writes are write-through: a changed file uploads when its writer closes it, and a rename onto a real name is the commit point
/// of a save. Deletes and renames to temp names wait out the settle window, so a save pattern can put its new content into the
/// original item (same ID, versions and sharing). Temp and lock files never reach SharePoint.
/// The write gate orders changes to local state; transfers (uploads, downloads for a write) let it go, and only operations on the same
/// file wait.
/// </summary>
public sealed partial class DriveEngine
{
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly SemaphoreSlim uploadSlots = new(Math.Max(1, options.UploadConcurrency));
    readonly Dictionary<PendingFile, Task> backgroundUploads = [];

    /// <summary>When each item was last uploaded by a close, for <see cref="DriveEngineOptions.RepeatSaveWindow"/>.</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> recentUploads = new(StringComparer.Ordinal);

    /// <summary>Keys with a transfer running without the write gate, until it ends. Guarded by the write gate.</summary>
    readonly Dictionary<string, Task> transfers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Items saved lately, so handles opened before a save read the saved content.</summary>
    readonly ConcurrentDictionary<string, (DriveItemInfo Item, DateTimeOffset At)> saved = new(StringComparer.Ordinal);

    /// <summary>The staged content each entry handed out refers to.</summary>
    readonly ConditionalWeakTable<FsEntry, PendingFile> bindings = new();

    static readonly TimeSpan SavedFollowTime = TimeSpan.FromHours(1);
    DateTimeOffset savedPruneAt;

    sealed record WriteTarget(Resolved Resolved, DriveMount Drive, string DriveId)
    {
        public string DrivePath => Resolved.DrivePath;
    }

    public async Task<FsEntry> CreateAsync(string path, bool isDirectory, CancellationToken ct)
    {
        var target = await WritableAsync(path, ct);
        await writeGate.WaitAsync(ct);
        try
        {
            var key = Key(target.DriveId, target.DrivePath);
            await SettleAsync(key);
            var local = overlay.GetValueOrDefault(key);
            if (local is PendingFile or AliasItem || (local is null && await LookupAsync(target.DriveId, target.DrivePath, ct) is not null))
            {
                throw new FsException(FsError.NameCollision, path);
            }

            if (isDirectory)
            {
                var folder = await api.CreateFolderAsync(target.DriveId, ParentOf(target.DrivePath), NameOf(target.DrivePath), ct);
                Upsert(target.DriveId, target.DrivePath, folder);
                // Empty, as it was just made: files created in it need no lookups
                listings[key] = Listing.Loaded([], Now, Now, folder.Id);
                return ItemEntry(target.Resolved.Path, target.Drive, target.DriveId, target.DrivePath, folder);
            }

            // A file created where one was just deleted takes over that item
            var pending = new PendingFile(target.DriveId, target.DrivePath, NewStagingFile(), (local as GoneItem)?.Item, Now) { Dirty = true };
            overlay[key] = pending;
            Journal(pending);
            return PendingEntry(target.Resolved.Path, target.Drive, pending);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>Stages the file for changes and returns the staging path. Content is copied in unless <paramref name="truncate"/>.</summary>
    public async Task<string> OpenForWriteAsync(FsEntry file, bool truncate, CancellationToken ct) => (await StageAsync(file, truncate, ct)).StagingPath!;

    /// <summary>
    /// Stages the file for changes: content is copied in unless <paramref name="truncate"/>. Returns the staged entry; a handle that
    /// keeps it follows this content (<see cref="Follow"/>). The download runs without the write gate.
    /// </summary>
    public async Task<FsEntry> StageAsync(FsEntry file, bool truncate, CancellationToken ct)
    {
        if (file.IsDirectory)
        {
            throw new FsException(FsError.AccessDenied, file.Path);
        }
        var target = await WritableAsync(file.Path, ct);
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
                            await AbandonAsync(pending, progress);
                        }
                        pending.HasWrites = false;
                        using var stream = new FileStream(pending.StagingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        stream.SetLength(0);
                        pending.Dirty = true;
                        pending.Modified = Now;
                    }
                    return PendingEntry(target.Resolved.Path, target.Drive, pending);
                case GoneItem:
                    throw new FsException(FsError.NotFound, file.Path);
                case AliasItem aliased:
                    alias = aliased;
                    item = aliased.Item;
                    break;
                default:
                    item = await LookupAsync(target.DriveId, target.DrivePath, ct) ?? throw new FsException(FsError.NotFound, file.Path);
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
            var staged = new PendingFile(target.DriveId, target.DrivePath, staging, item, Now) { Dirty = truncate, Alias = alias };
            overlay[key] = staged;
            if (truncate)
            {
                Journal(staged);
            }
            return PendingEntry(target.Resolved.Path, target.Drive, staged);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>
    /// What an entry handed out earlier refers to now, for a handle that keeps it. A staged entry follows its content through renames
    /// and into the item it was uploaded as (a conflict copy included); an item entry picks up staged or saved content of the same item.
    /// Throws <see cref="FsError.Invalid"/> when the staged content was deleted or replaced.
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
        if (entry.ItemId is { } id && saved.TryGetValue(id, out var known) && (known.Item.CTag ?? known.Item.ETag) != entry.ContentTag)
        {
            return ItemEntry(entry.Path, drive, driveId, entry.DrivePath, known.Item);
        }
        return entry;
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
            if (!pending.Journaled)
            {
                Journal(pending);
            }
        }
    }

    /// <summary>For writes no close may follow (paging I/O of a mapped file): uploads the file when no write came for <see cref="DriveEngineOptions.PagingCommitDelay"/>.</summary>
    public void CommitLater(FsEntry file)
    {
        if (PendingOf(file) is { } pending && !LocalOnlyNames.IsLocalOnly(pending.Name))
        {
            pending.CommitAt = Now + options.PagingCommitDelay;
        }
    }

    /// <summary>
    /// Uploads a changed file when its writer closes it. Close can't fail, so a lock or conflict ends in a conflict copy.
    /// New files go to the background uploads when that is on; a file saved again within RepeatSaveWindow uploads at its end.
    /// </summary>
    public async Task CommitAsync(string path, CancellationToken ct)
    {
        if (ns.Resolve(path) is not { Drive: { } drive } resolved || resolved.DrivePath.Length == 0)
        {
            return;
        }
        var driveId = await drive.GetDriveIdAsync(ct);
        await writeGate.WaitAsync(ct);
        try
        {
            var key = Key(driveId, resolved.DrivePath);
            await SettleAsync(key);
            if (overlay.GetValueOrDefault(key) is not PendingFile { Dirty: true } pending || LocalOnlyNames.IsLocalOnly(pending.Name))
            {
                return;
            }
            pending.Queued = true;
            if (pending.Base is null && pending.Progress is null && options.UploadNewFilesInBackground)
            {
                FlushToDisk(pending.StagingPath); // journaled since it was created
                QueueUpload(pending);
                return;
            }
            if (pending.Base is { } baseItem && options.RepeatSaveWindow > TimeSpan.Zero && recentUploads.TryGetValue(baseItem.Id, out var last)
                && Now - last < options.RepeatSaveWindow)
            {
                FlushToDisk(pending.StagingPath);
                pending.CommitAt ??= last + options.RepeatSaveWindow;
                return;
            }
            await UploadPendingAsync(pending, ct);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>
    /// Called with the write gate held. While a transfer runs at one of the keys, lets the gate go until it ended, so the caller sees
    /// its outcome.
    /// </summary>
    async Task SettleAsync(params string[] keys)
    {
        while (keys.Select(transfers.GetValueOrDefault).FirstOrDefault(t => t is not null) is { } transfer)
        {
            writeGate.Release();
            try
            {
                await transfer;
            }
            finally
            {
                await writeGate.WaitAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// Runs a transfer without the write gate: called with it held, returns with it held. Operations on the keys wait for it
    /// (<see cref="SettleAsync"/>) and due work skips them, so their local state is as before when it returns.
    /// </summary>
    async Task<T> TransferAsync<T>(Func<Task<T>> transfer, params string[] keys)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        foreach (var key in keys)
        {
            transfers[key] = done.Task;
        }
        writeGate.Release();
        try
        {
            return await transfer();
        }
        finally
        {
            await writeGate.WaitAsync(CancellationToken.None);
            foreach (var key in keys)
            {
                transfers.Remove(key);
            }
            done.SetResult();
        }
    }

    /// <summary>Uploads the file in the background, UploadConcurrency at a time; once per file at a time.</summary>
    void QueueUpload(PendingFile pending)
    {
        lock (backgroundUploads)
        {
            if (!backgroundUploads.TryGetValue(pending, out var running) || running.IsCompleted)
            {
                backgroundUploads[pending] = Task.Run(() => BackgroundUploadAsync(pending));
            }
        }
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
                await SettleAsync(pending.Key);
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
                writeGate.Release();
            }
        }
        finally
        {
            uploadSlots.Release();
        }
    }

    /// <summary>Background uploads queued or running.</summary>
    Task[] RunningUploads()
    {
        lock (backgroundUploads)
        {
            foreach (var done in backgroundUploads.Where(u => u.Value.IsCompleted).Select(u => u.Key).ToList())
            {
                backgroundUploads.Remove(done);
            }
            return [.. backgroundUploads.Values];
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

    /// <summary>Folders go at once; files are hidden now and deleted after the settle window unless a save claims them.</summary>
    public async Task DeleteAsync(string path, CancellationToken ct)
    {
        var target = await WritableAsync(path, ct);
        await writeGate.WaitAsync(ct);
        try
        {
            var key = Key(target.DriveId, target.DrivePath);
            await SettleAsync(key);
            switch (overlay.GetValueOrDefault(key))
            {
                case PendingFile { Alias: { } staged } pending:
                    // A renamed-away original that was opened for writing: deleted like the alias it was
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
                await api.DeleteAsync(target.DriveId, item.Id, ct);
                Invalidate(target.DriveId, target.DrivePath);
                Forget(target.DriveId, target.DrivePath);
                Log($"deleted folder {target.DrivePath}");
                return;
            }
            overlay[key] = new GoneItem(target.DriveId, target.DrivePath, item, Now + options.SettleWindow, deleteOnExpiry: true);
        }
        finally
        {
            writeGate.Release();
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

        await writeGate.WaitAsync(ct);
        try
        {
            var driveId = from.DriveId;
            await SettleAsync(Key(driveId, from.DrivePath), Key(driveId, to.DrivePath));
            var fromLocal = overlay.GetValueOrDefault(Key(driveId, from.DrivePath));
            var sameName = from.DrivePath.Equals(to.DrivePath, StringComparison.OrdinalIgnoreCase);
            var toLocal = sameName ? null : overlay.GetValueOrDefault(Key(driveId, to.DrivePath));
            var toRemote = sameName || toLocal is not null ? null : await LookupAsync(driveId, to.DrivePath, ct);
            if (!replace && (toLocal is PendingFile or AliasItem || toRemote is not null))
            {
                throw new FsException(FsError.NameCollision, destination);
            }
            var toItem = toLocal switch
            {
                GoneItem gone => gone.Item,
                PendingFile pending => pending.Base,
                AliasItem alias => alias.Item,
                _ => toRemote,
            };

            switch (fromLocal)
            {
                case PendingFile pending:
                    await RenamePendingAsync(pending, to.DrivePath, toItem, toLocal, ct);
                    return;
                case AliasItem alias:
                    await RenameAliasAsync(alias, to.DrivePath, toLocal, ct);
                    return;
                case GoneItem:
                    throw new FsException(FsError.NotFound, source);
            }

            var item = await LookupAsync(driveId, from.DrivePath, ct) ?? throw new FsException(FsError.NotFound, source);
            if (!item.IsFolder && !sameName && LocalOnlyNames.IsLocalOnly(NameOf(to.DrivePath)))
            {
                // Saving: the original steps aside under a temp name. Kept local; restored unless new content claims it.
                DropLocal(toLocal);
                overlay[Key(driveId, from.DrivePath)] = new GoneItem(driveId, from.DrivePath, item, Now + options.SettleWindow, deleteOnExpiry: false);
                overlay[Key(driveId, to.DrivePath)] = new AliasItem(driveId, to.DrivePath, item, from.DrivePath, Now + options.SettleWindow);
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
                        return await api.UploadAsync(driveId, UploadTarget.Existing(toItem), file, ct);
                    }, Key(driveId, from.DrivePath), Key(driveId, to.DrivePath));
                }
                catch
                {
                    TryDelete(file);
                    throw;
                }
                content.Adopt(driveId, updated, file);
                DropLocal(toLocal);
                Claimed(driveId, updated);
                Upsert(driveId, to.DrivePath, updated);
                overlay[Key(driveId, from.DrivePath)] = new GoneItem(driveId, from.DrivePath, item, Now + options.SettleWindow, deleteOnExpiry: true);
                Log($"replaced {to.DrivePath} with {from.DrivePath}");
                return;
            }
            if (item.IsFolder && toItem is not null)
            {
                throw new FsException(FsError.AccessDenied, destination);
            }

            var moved = await api.MoveAsync(driveId, item.Id, await FolderIdAsync(driveId, ParentOf(to.DrivePath), ct), NameOf(to.DrivePath), ct);
            if (item.IsFolder)
            {
                Invalidate(driveId, from.DrivePath);
            }
            Forget(driveId, from.DrivePath);
            Upsert(driveId, to.DrivePath, moved);
            Log($"renamed {from.DrivePath} to {to.DrivePath} ({moved.Id})");
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>
    /// Applies deletes whose settle window passed, restores originals nothing claimed and starts uploads that are due (retries,
    /// deferred commits). With <paramref name="all"/> everything is due and this waits for every upload (unmount). A failure stays with
    /// its file: it never stops the rest.
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
                try
                {
                    await ProcessAsync(local, all, ct);
                }
                catch (Exception e)
                {
                    Log($"error: {local.DrivePath}: {e.Message}");
                }
            }
        }
        finally
        {
            writeGate.Release();
        }
        if (all)
        {
            await WaitForUploadsAsync();
        }
    }

    /// <summary>One item of <see cref="ProcessDueAsync"/>, with the write gate held.</summary>
    async Task ProcessAsync(LocalState local, bool all, CancellationToken ct)
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
            case GoneItem gone when all || gone.Deadline <= Now:
                try
                {
                    await api.DeleteAsync(gone.DriveId, gone.Item.Id, ct);
                }
                catch (RemoteException e) when (e.Error == RemoteError.NotFound)
                {
                }
                catch (Exception e) when (IsTransient(e))
                {
                    gone.Deadline = Now + RetryDelay(++gone.Failures);
                    Log($"delete of {gone.DrivePath} failed, retrying in {RetryDelay(gone.Failures).TotalMinutes:0.#} min: {Short(e)}");
                    break;
                }
                catch (Exception e)
                {
                    // Refused (locked, no permission to delete): the file shows again
                    DropLocal(gone);
                    Report(gone.DriveId, gone.DrivePath, ChangeKind.Added, false);
                    Log($"error: delete of {gone.DrivePath} refused, it is back: {Short(e)}");
                    Notify(NoticeKind.NotDeleted, gone.DriveId, gone.DrivePath, Short(e));
                    break;
                }
                DropLocal(gone);
                Forget(gone.DriveId, gone.DrivePath);
                Log($"deleted {gone.DrivePath} ({gone.Item.Id})");
                break;
            case PendingFile { Dirty: true, RetryAt: { } retryAt } pending when all || retryAt <= Now:
                pending.RetryAt = null;
                pending.Queued = true;
                QueueUpload(pending);
                break;
            case PendingFile { Dirty: true, RetryAt: null, CommitAt: { } commitAt } pending when all || commitAt <= Now:
                pending.CommitAt = null;
                pending.Queued = true;
                QueueUpload(pending);
                break;
        }
    }

    /// <summary>Changed files that are not uploaded yet, for reporting at unmount.</summary>
    public IReadOnlyList<string> Unsaved() =>
        overlay.Values.OfType<PendingFile>().Where(p => p.Dirty && !LocalOnlyNames.IsLocalOnly(p.Name)).Select(p => $"{p.DrivePath} ({p.StagingPath})").ToList();

    /// <summary>
    /// Changed files waiting to upload: a close (or a deferred commit) asked for the upload and it hasn't happened yet, failed ones
    /// included. Files still open for writing don't count.
    /// </summary>
    public int WaitingToUpload() =>
        overlay.Values.OfType<PendingFile>().Count(p => p.Dirty && p.Queued && !LocalOnlyNames.IsLocalOnly(p.Name));

    async Task RenamePendingAsync(PendingFile pending, string to, DriveItemInfo? toItem, LocalState? toLocal, CancellationToken ct)
    {
        var driveId = pending.DriveId;
        var from = pending.DrivePath;
        if (pending.Progress is { } progress)
        {
            await AbandonAsync(pending, progress);
        }
        if (LocalOnlyNames.IsLocalOnly(NameOf(to)))
        {
            DropLocal(toLocal);
            overlay.TryRemove(pending.Key, out _);
            pending.DrivePath = to;
            overlay[pending.Key] = pending;
            Journal(pending);
            return;
        }

        // Commit point of a save: content goes into the item that owns the destination, else the file's own item, else a new file.
        // Failures reach the application, which keeps its temp file and can retry.
        var writes = pending.Writes;
        var originalBase = pending.Base;
        DriveItemInfo? uploaded = null;
        DriveItemInfo item;
        try
        {
            item = await TransferAsync(async () =>
            {
                if ((toItem ?? originalBase) is not { } into)
                {
                    return await api.UploadAsync(driveId, UploadTarget.New(ParentOf(to), NameOf(to)), pending.StagingPath, ct);
                }
                uploaded = await api.UploadAsync(driveId, UploadTarget.Existing(into), pending.StagingPath, ct);
                return toItem is null ? await api.MoveAsync(driveId, uploaded.Id, await FolderIdAsync(driveId, ParentOf(to), ct), NameOf(to), ct) : uploaded;
            }, pending.Key, Key(driveId, to));
        }
        catch when (uploaded is not null)
        {
            // The content is in the file's own item, the move failed: later uploads go into the item as it is now
            pending.Base = uploaded;
            Upsert(driveId, from, uploaded);
            Journal(pending);
            throw;
        }
        if (toItem is null && originalBase is not null)
        {
            Forget(driveId, from);
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
                Upsert(driveId, to, item);
                Journal(pending);
                pending.CommitAt ??= Now + options.PagingCommitDelay;
            }
            else
            {
                Uploaded(pending, item, to);
            }
        }
        finally
        {
            pending.Writing.ExitWriteLock();
        }
        if (toItem is not null && originalBase is { } replaced && replaced.Id != toItem.Id)
        {
            overlay[Key(driveId, from)] = new GoneItem(driveId, from, replaced, Now + options.SettleWindow, deleteOnExpiry: true);
        }
        Log($"saved {to} into {item.Id} (from {NameOf(from)})");
    }

    async Task RenameAliasAsync(AliasItem alias, string to, LocalState? toLocal, CancellationToken ct)
    {
        var driveId = alias.DriveId;
        if (to.Equals(alias.OriginalPath, StringComparison.OrdinalIgnoreCase))
        {
            // Renamed back: the save didn't happen
            DropLocal(alias);
            DropLocal(toLocal);
            return;
        }
        if (LocalOnlyNames.IsLocalOnly(NameOf(to)))
        {
            DropLocal(toLocal);
            overlay.TryRemove(alias.Key, out _);
            alias.DrivePath = to;
            overlay[alias.Key] = alias;
            return;
        }

        var moved = await api.MoveAsync(driveId, alias.Item.Id, await FolderIdAsync(driveId, ParentOf(to), ct), NameOf(to), ct);
        DropLocal(alias);
        if (overlay.GetValueOrDefault(Key(driveId, alias.OriginalPath)) is GoneItem original && original.Item.Id == alias.Item.Id)
        {
            DropLocal(original);
        }
        DropLocal(toLocal);
        Forget(driveId, alias.OriginalPath);
        Upsert(driveId, to, moved);
    }

    /// <summary>What became of an upload: the item the content went into, at which path and how, or the failure.</summary>
    sealed record UploadOutcome(DriveItemInfo? Item, string DrivePath, UploadKind Kind, Exception? Cause);

    enum UploadKind { Saved, AsNew, ConflictCopy, Failed }

    /// <summary>
    /// Uploads a pending file. Called with the write gate held; lets it go during the transfer, so other files go on meanwhile, and
    /// holds it again before applying the outcome. Operations on this file wait for it (<see cref="SettleAsync"/>). A failed upload
    /// doesn't throw: the file stays staged and is retried with back-off.
    /// </summary>
    async Task UploadPendingAsync(PendingFile pending, CancellationToken ct)
    {
        if (LocalOnlyNames.IsLocalOnly(pending.Name))
        {
            // Renamed to a temp name after its close: stays local
            pending.CommitAt = pending.RetryAt = null;
            pending.Queued = false;
            return;
        }
        var target = TargetOf(pending);
        var writes = pending.Writes;
        var outcome = await TransferAsync(() => SendAsync(pending, target, ct), pending.Key);

        if (outcome.Kind == UploadKind.Failed)
        {
            pending.RetryAt = Now + RetryDelay(++pending.Failures);
            Log($"upload of {pending.DrivePath} failed, retrying in {RetryDelay(pending.Failures).TotalMinutes:0.#} min: {Short(outcome.Cause!)}");
            if (!pending.Waiting)
            {
                pending.Waiting = true;
                Notify(NoticeKind.UploadWaiting, pending.DriveId, pending.DrivePath, Short(outcome.Cause!));
            }
            FlushToDisk(pending.StagingPath);
            return;
        }

        var item = outcome.Item!;
        var original = pending.DrivePath;
        pending.Writing.EnterWriteLock();
        try
        {
            if (outcome.Kind == UploadKind.ConflictCopy)
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
                Upsert(pending.DriveId, outcome.DrivePath, item);
                Journal(pending);
                pending.CommitAt ??= Now + options.PagingCommitDelay;
                Log($"saved {outcome.DrivePath} into {item.Id}; it changed meanwhile, uploading again");
            }
            else
            {
                Uploaded(pending, item, outcome.DrivePath);
                Log($"saved {outcome.DrivePath} into {item.Id}{(target.ItemId is null || outcome.Kind != UploadKind.Saved ? " (new)" : "")}");
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
                Log($"{original} is {why} on the server: saved as {item.Name}");
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
    /// The transfer of <see cref="UploadPendingAsync"/>, without the write gate: into the item, else as a new file under its name when
    /// the item was deleted on the server, else as a conflict copy next to it.
    /// </summary>
    async Task<UploadOutcome> SendAsync(PendingFile pending, UploadTarget target, CancellationToken ct)
    {
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
            try
            {
                return new(await FinishProgressiveAsync(pending, ct) ?? await api.UploadAsync(pending.DriveId, target, pending.StagingPath, ct),
                    pending.DrivePath, UploadKind.Saved, null);
            }
            catch (RemoteException e) when (target.ItemId is not null && e.Error is RemoteError.NotFound or RemoteError.Gone)
            {
                try
                {
                    var created = await api.UploadAsync(pending.DriveId, UploadTarget.New(ParentOf(pending.DrivePath), pending.Name), pending.StagingPath, ct);
                    return new(created, pending.DrivePath, UploadKind.AsNew, e);
                }
                catch (RemoteException again) when (again.Error == RemoteError.Conflict)
                {
                    return await ConflictCopyAsync(pending, e, ct);
                }
            }
            catch (RemoteException e) when (e.Error is RemoteError.Conflict or RemoteError.Locked)
            {
                return await ConflictCopyAsync(pending, e, ct);
            }
        }
        catch (Exception e)
        {
            return new(null, pending.DrivePath, UploadKind.Failed, e);
        }
    }

    /// <summary>Uploads the staged content next to the file under a conflict name; under another one when that is taken too.</summary>
    async Task<UploadOutcome> ConflictCopyAsync(PendingFile pending, Exception cause, CancellationToken ct)
    {
        var parent = ParentOf(pending.DrivePath);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var copy = await api.UploadAsync(pending.DriveId, UploadTarget.New(parent, ConflictName(pending.Name, attempt)), pending.StagingPath, ct);
                return new(copy, JoinDrivePath(parent, copy.Name), UploadKind.ConflictCopy, cause);
            }
            catch (RemoteException e) when (e.Error == RemoteError.Conflict && attempt < 10)
            {
            }
        }
    }

    /// <summary>
    /// For a file recovered after a crash: the item at its path when it has the staged content already (the upload went through before
    /// the journal was removed), else null.
    /// </summary>
    async Task<DriveItemInfo?> AlreadyUploadedAsync(PendingFile pending, CancellationToken ct)
    {
        var current = await api.GetItemAsync(pending.DriveId, pending.DrivePath, ct);
        if (current is null || current.IsFolder || current.Size != new FileInfo(pending.StagingPath).Length
            || (pending.Base is { } baseItem && (current.Id != baseItem.Id || current.ETag == baseItem.ETag)))
        {
            return null;
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

    /// <summary>Waits for the background uploads queued or running now.</summary>
    public async Task WaitForUploadsAsync()
    {
        while (RunningUploads() is { Length: > 0 } running)
        {
            await Task.WhenAll(running);
        }
    }

    /// <summary>The pending content is now <paramref name="item"/> at <paramref name="drivePath"/>. Called with its write lock held.</summary>
    void Uploaded(PendingFile pending, DriveItemInfo item, string drivePath)
    {
        recentUploads[item.Id] = Now;
        saved[item.Id] = (item, Now);
        pending.Retire(item, drivePath);
        overlay.TryRemove(new KeyValuePair<string, LocalState>(pending.Key, pending));
        if (overlay.GetValueOrDefault(Key(pending.DriveId, drivePath)) is GoneItem gone)
        {
            DropLocal(gone);
        }
        Claimed(pending.DriveId, item);
        Upsert(pending.DriveId, drivePath, item);
        content.Adopt(pending.DriveId, item, pending.StagingPath);
        TryDelete(JournalOf(pending.StagingPath));
    }

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
            TryDelete(JournalOf(pending.StagingPath));
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
            return (await LookupAsync(driveId, folderPath, ct))?.Id ?? throw new FsException(FsError.NotFound, folderPath);
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

    string ConflictName(string name, int attempt) =>
        $"{Path.GetFileNameWithoutExtension(name)} (conflict {Now.ToLocalTime():yyyy-MM-dd HHmmss}{(attempt > 1 ? $" {attempt}" : "")}){Path.GetExtension(name)}";

    /// <summary>Forces a staged file to disk, so a close acknowledged before its upload survives a power loss.</summary>
    void FlushToDisk(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            file.Flush(flushToDisk: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log($"error: flushing {path}: {e.Message}");
        }
    }

    /// <summary>Retries back off: RetryInterval, then twice as long each time, up to 15 minutes.</summary>
    TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(15).Ticks, options.RetryInterval.Ticks << Math.Clamp(failures - 1, 0, 10)));

    /// <summary>What went wrong, in words a user or admin can act on.</summary>
    static string Short(Exception e) => e is RemoteException { Error: RemoteError.ReadOnly }
        ? "SharePoint has the library read-only for now (maintenance or a site move)"
        : e.Message;

    /// <summary>Worth retrying: network and sign-in errors (the token source's own exceptions) and service states that pass.</summary>
    static bool IsTransient(Exception e) =>
        e is RemoteException remote ? remote.Error is RemoteError.Throttled or RemoteError.Unavailable or RemoteError.ReadOnly : e is not FsException;

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

    void Notify(NoticeKind kind, string driveId, string drivePath, string? detail) =>
        options.Notice?.Invoke(new EngineNotice(kind, NameOf(drivePath), VolumePath(driveId, drivePath), detail));

    /// <summary>Where a drive path shows on the volume; null when its library isn't mounted.</summary>
    string? VolumePath(string driveId, string drivePath) =>
        ns.VolumePathOf(driveId) is { } top ? Join(top, drivePath.Replace('/', '\\')) : null;
}
