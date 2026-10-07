using System.Collections.Concurrent;

namespace Vfs365.Core.Drive;

/// <summary>
/// Writes are write-through: a changed file uploads when its writer closes it, and a rename onto a real name is the commit point
/// of a save. Deletes and renames to temp names wait out the settle window, so a save pattern can put its new content into the
/// original item (same ID, versions and sharing). Temp and lock files never reach SharePoint.
/// The write gate orders changes to local state; uploads let it go while they transfer, and only operations on the same file wait.
/// </summary>
public sealed partial class DriveEngine
{
    readonly SemaphoreSlim writeGate = new(1, 1);
    readonly SemaphoreSlim uploadSlots = new(Math.Max(1, options.UploadConcurrency));
    readonly Dictionary<PendingFile, Task> backgroundUploads = [];

    /// <summary>When each item was last uploaded by a close, for <see cref="DriveEngineOptions.RepeatSaveWindow"/>.</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> recentUploads = new(StringComparer.Ordinal);

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
    public async Task<string> OpenForWriteAsync(FsEntry file, bool truncate, CancellationToken ct)
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
                    return pending.StagingPath;
                case GoneItem:
                    throw new FsException(FsError.NotFound, file.Path);
                case AliasItem alias:
                    item = alias.Item;
                    break;
                default:
                    item = await LookupAsync(target.DriveId, target.DrivePath, ct) ?? throw new FsException(FsError.NotFound, file.Path);
                    break;
            }

            var staging = NewStagingFile();
            if (!truncate)
            {
                await content.CopyToAsync(target.DriveId, item.Id, item.CTag ?? item.ETag, item.Size, staging, ct);
            }
            var staged = new PendingFile(target.DriveId, target.DrivePath, staging, item, Now) { Dirty = truncate };
            overlay[key] = staged;
            if (truncate)
            {
                Journal(staged);
            }
            return staging;
        }
        finally
        {
            writeGate.Release();
        }
    }

    public void MarkDirty(FsEntry file)
    {
        if (file.DriveId is not null && overlay.GetValueOrDefault(Key(file.DriveId, file.DrivePath)) is PendingFile pending)
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
        if (file.DriveId is not null && overlay.GetValueOrDefault(Key(file.DriveId, file.DrivePath)) is PendingFile pending && !LocalOnlyNames.IsLocalOnly(pending.Name))
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
            if (pending.Base is null && pending.Progress is null && options.UploadNewFilesInBackground)
            {
                QueueUpload(pending); // journaled since it was created
                return;
            }
            if (pending.Base is { } baseItem && options.RepeatSaveWindow > TimeSpan.Zero && recentUploads.TryGetValue(baseItem.Id, out var last)
                && Now - last < options.RepeatSaveWindow)
            {
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
    /// Called with the write gate held. While a file at one of the keys uploads, lets the gate go until that upload ended, so the
    /// caller sees its outcome.
    /// </summary>
    async Task SettleAsync(params string[] keys)
    {
        while (keys.Select(k => overlay.GetValueOrDefault(k)).OfType<PendingFile>().FirstOrDefault(p => p.Uploading is { IsCompleted: false })?.Uploading is { } upload)
        {
            writeGate.Release();
            try
            {
                await upload;
            }
            finally
            {
                await writeGate.WaitAsync(CancellationToken.None);
            }
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
                case PendingFile pending:
                    DropLocal(pending);
                    if (pending.Base is { } baseItem)
                    {
                        overlay[key] = new GoneItem(target.DriveId, target.DrivePath, baseItem, Now + options.SettleWindow, deleteOnExpiry: true);
                    }
                    return;
                case AliasItem alias:
                    DropLocal(alias);
                    if (!alias.Claimed && overlay.GetValueOrDefault(Key(target.DriveId, alias.OriginalPath)) is GoneItem original)
                    {
                        original.DeleteOnExpiry = true;
                        original.Deadline = Now + options.SettleWindow;
                    }
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
                    await content.CopyToAsync(driveId, item.Id, item.CTag ?? item.ETag, item.Size, file, ct);
                    updated = await api.UploadAsync(driveId, UploadTarget.Existing(toItem), file, ct);
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
    /// deferred commits). With <paramref name="all"/> everything is due and this waits for every upload (unmount).
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
            foreach (var local in overlay.Values.ToList())
            {
                if (!ReferenceEquals(overlay.GetValueOrDefault(local.Key), local) || local is PendingFile { Uploading: not null })
                {
                    continue;
                }
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
                        DropLocal(gone);
                        Forget(gone.DriveId, gone.DrivePath);
                        Log($"deleted {gone.DrivePath} ({gone.Item.Id})");
                        break;
                    case PendingFile { Dirty: true, RetryAt: { } retryAt } pending when all || retryAt <= Now:
                        pending.RetryAt = null;
                        QueueUpload(pending);
                        break;
                    case PendingFile { Dirty: true, RetryAt: null, CommitAt: { } commitAt } pending when all || commitAt <= Now:
                        pending.CommitAt = null;
                        QueueUpload(pending);
                        break;
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

    /// <summary>Changed files that are not uploaded yet, for reporting at unmount.</summary>
    public IReadOnlyList<string> Unsaved() =>
        overlay.Values.OfType<PendingFile>().Where(p => p.Dirty && !LocalOnlyNames.IsLocalOnly(p.Name)).Select(p => $"{p.DrivePath} ({p.StagingPath})").ToList();

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
        DriveItemInfo item;
        if ((toItem ?? pending.Base) is { } into)
        {
            item = await api.UploadAsync(driveId, UploadTarget.Existing(into), pending.StagingPath, ct);
            if (toItem is null)
            {
                item = await api.MoveAsync(driveId, item.Id, await FolderIdAsync(driveId, ParentOf(to), ct), NameOf(to), ct);
                Forget(driveId, from);
            }
        }
        else
        {
            item = await api.UploadAsync(driveId, UploadTarget.New(ParentOf(to), NameOf(to)), pending.StagingPath, ct);
        }

        DropLocal(toLocal);
        Uploaded(pending, item, to);
        if (toItem is not null && pending.Base is { } replaced && replaced.Id != toItem.Id)
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

    /// <summary>
    /// Uploads a pending file. Called with the write gate held; lets it go during the transfer, so other files go on meanwhile, and
    /// holds it again before applying the outcome. Operations on this file wait for it (<see cref="SettleAsync"/>).
    /// </summary>
    async Task UploadPendingAsync(PendingFile pending, CancellationToken ct)
    {
        var target = TargetOf(pending);
        var writes = pending.Writes;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Uploading = finished.Task;
        DriveItemInfo? item = null, copy = null;
        RemoteException? conflict = null;
        Exception? failure = null;
        writeGate.Release();
        try
        {
            try
            {
                item = await FinishProgressiveAsync(pending, ct) ?? await api.UploadAsync(pending.DriveId, target, pending.StagingPath, ct);
            }
            catch (RemoteException e) when (e.Error is RemoteError.Conflict or RemoteError.Locked)
            {
                conflict = e;
                copy = await api.UploadAsync(pending.DriveId, UploadTarget.New(ParentOf(pending.DrivePath), ConflictName(pending.Name)), pending.StagingPath, ct);
            }
        }
        catch (Exception e) when (IsTransient(e))
        {
            failure = e;
        }
        finally
        {
            await writeGate.WaitAsync(CancellationToken.None);
            pending.Uploading = null;
            finished.SetResult();
        }

        if (failure is not null)
        {
            pending.RetryAt = Now + RetryDelay(++pending.Failures);
            Log($"upload of {pending.DrivePath} failed, retrying in {RetryDelay(pending.Failures).TotalMinutes:0.#} min: {Short(failure)}");
            if (!pending.Waiting)
            {
                pending.Waiting = true;
                Notify(NoticeKind.UploadWaiting, pending.DriveId, pending.DrivePath, Short(failure));
            }
        }
        else if (copy is not null)
        {
            DropLocal(pending);
            Invalidate(pending.DriveId, pending.DrivePath);
            Upsert(pending.DriveId, JoinDrivePath(ParentOf(pending.DrivePath), copy.Name), copy);
            Log($"{pending.DrivePath} is {(conflict!.Error == RemoteError.Locked ? "locked" : "changed")} on the server: saved as {copy.Name}");
            Notify(NoticeKind.ConflictCopy, pending.DriveId, pending.DrivePath, copy.Name);
        }
        else if (pending.Writes != writes)
        {
            // Written to while it uploaded: the item has the earlier content, the rest follows with the next commit
            pending.Base = item;
            Upsert(pending.DriveId, pending.DrivePath, item!);
            pending.CommitAt ??= Now + options.PagingCommitDelay;
            Log($"saved {pending.DrivePath} into {item!.Id}; it changed meanwhile, uploading again");
        }
        else
        {
            Uploaded(pending, item!, pending.DrivePath);
            Log($"saved {pending.DrivePath} into {item!.Id}{(target.ItemId is null ? " (new)" : "")}");
            if (pending.Waiting)
            {
                Notify(NoticeKind.UploadDone, pending.DriveId, pending.DrivePath, null);
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

    /// <summary>The pending content is now <paramref name="item"/> at <paramref name="drivePath"/>.</summary>
    void Uploaded(PendingFile pending, DriveItemInfo item, string drivePath)
    {
        recentUploads[item.Id] = Now;
        overlay.TryRemove(pending.Key, out _);
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

    string ConflictName(string name) =>
        $"{Path.GetFileNameWithoutExtension(name)} (conflict {Now.ToLocalTime():yyyy-MM-dd HHmm}){Path.GetExtension(name)}";

    /// <summary>Retries back off: RetryInterval, then twice as long each time, up to 15 minutes.</summary>
    TimeSpan RetryDelay(int failures) =>
        TimeSpan.FromTicks(Math.Min(TimeSpan.FromMinutes(15).Ticks, options.RetryInterval.Ticks << Math.Clamp(failures - 1, 0, 10)));

    /// <summary>What went wrong, in words a user or admin can act on.</summary>
    static string Short(Exception e) => e is RemoteException { Error: RemoteError.ReadOnly }
        ? "SharePoint has the library read-only for now (maintenance or a site move)"
        : e.Message;

    static bool IsTransient(Exception e) =>
        e is HttpRequestException or TaskCanceledException or TimeoutException || e is RemoteException { Error: RemoteError.Throttled or RemoteError.Unavailable or RemoteError.ReadOnly };

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
        options.Notice?.Invoke(new EngineNotice(kind, NameOf(drivePath),
            ns.VolumePathOf(driveId) is { } top ? Join(top, drivePath.Replace('/', '\\')) : null, detail));
}
