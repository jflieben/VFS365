using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vfs365.Core.Drive;

public sealed record DriveEngineOptions
{
    /// <summary>Local files holding content that is being changed.</summary>
    public required string StagingDirectory { get; init; }

    /// <summary>How long a folder listing is trusted when the drive's change feed isn't keeping it current. After that it still shows while it is read again.</summary>
    public TimeSpan ListingTtl { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan LookupTtl { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Remembered "doesn't exist" answers, so probes for desktop.ini and the like stay local.</summary>
    public TimeSpan NotFoundTtl { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long deletes and renames to temp names stay local, so a save can put new content into the same item.</summary>
    public TimeSpan SettleWindow { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Quiet time after the last paging write before a memory-mapped file uploads.</summary>
    public TimeSpan PagingCommitDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>
    /// New files upload after their close returned (journaled first), next to each other, instead of before it returns. Saves into
    /// existing files stay write-through. Copies of many small files then finish at disk speed.
    /// </summary>
    public bool UploadNewFilesInBackground { get; init; }

    /// <summary>Uploads running at the same time in the background.</summary>
    public int UploadConcurrency { get; init; } = 4;

    /// <summary>
    /// A file closed again within this time of its last upload uploads once at the end of it (zero: every close uploads). An app
    /// that appends to a file and closes it each time then sends it once per window instead of on every append.
    /// </summary>
    public TimeSpan RepeatSaveWindow { get; init; }

    /// <summary>
    /// While files of a drive are read one after another (a copy out of the drive), the next small files of the same folder that
    /// weren't opened yet are downloaded ahead, at most this many (zero: none).
    /// </summary>
    public int ReadAheadFiles { get; init; }

    /// <summary>Files larger than this are never downloaded ahead; they stream as they are read.</summary>
    public long ReadAheadMaxSize { get; init; } = 4 << 20;

    /// <summary>A drive's change feed is read when the drive is used and this much time passed since the last read.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Drives used within <see cref="ActiveWindow"/> are also read this often by <see cref="DriveEngine.PollActiveAsync"/>.</summary>
    public TimeSpan BackgroundPollInterval { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan ActiveWindow { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>With a push channel up (<see cref="DriveEngine.SetPush"/>), changes are read on notification; this read is only a safety net.</summary>
    public TimeSpan PushedPollInterval { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>A drive used within HotWindow is read this often, push or not (zero: never); its changes show sooner than push brings them.</summary>
    public TimeSpan HotPollInterval { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan HotWindow { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A tree walk: this many folders of one drive start loading within WalkWindow, and the walk went at least two levels down
    /// (Explorer drawing folder thumbnails opens many folders side by side, which is no walk). Zero: never.
    /// </summary>
    public int WalkThreshold { get; init; } = 12;

    public TimeSpan WalkWindow { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>A walk ends when it opened no folder for this long (a big folder can take a while to read).</summary>
    public TimeSpan WalkIdle { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Folders loaded at the same time ahead of a walk (zero: none).</summary>
    public int PrefetchConcurrency { get; init; } = 2;

    /// <summary>
    /// Folders loaded ahead of a walk that it hasn't opened yet, at most (zero: nothing is loaded ahead). The walk would list them
    /// anyway, so this is the most a walk that stops early leaves unused.
    /// </summary>
    public int PrefetchAhead { get; init; } = 10;

    /// <summary>Multi-user database files: allowed, read-only or blocked.</summary>
    public DatabaseFilePolicy DatabaseFiles { get; init; } = DatabaseFilePolicy.Allow;


    /// <summary>Safety net: a listing the change feed maintains is still read again (in the background, when used) after this long.</summary>
    public TimeSpan MaintainedListingMaxAge { get; init; } = TimeSpan.FromHours(1);

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    public Action<string>? Log { get; init; }

    /// <summary>Changes that didn't come from this engine (remote edits, refreshed listings, new libraries), as volume paths.</summary>
    public Action<EngineChange>? Changed { get; init; }

    /// <summary>Things the user should hear about (conflict copies, uploads that keep failing), for notifications.</summary>
    public Action<EngineNotice>? Notice { get; init; }
}

public enum ChangeKind { Added, Removed, Modified }

public sealed record EngineChange(string Path, ChangeKind Kind, bool IsDirectory);

public enum NoticeKind
{
    /// <summary>The file changed or was locked on the server; the user's version was saved next to it as Detail (the copy's name).</summary>
    ConflictCopy,

    /// <summary>An upload failed and is retried; Detail is the reason. Reported once per file until it uploads.</summary>
    UploadWaiting,

    /// <summary>A file that was waiting uploaded after all.</summary>
    UploadDone,
}

/// <summary>Path is the volume path (null when its library isn't mounted any more).</summary>
public sealed record EngineNotice(NoticeKind Kind, string Name, string? Path, string? Detail);

/// <summary>Front-end-neutral file system over Graph: resolves paths, lists folders, fetches and saves content, with caching.</summary>
public sealed partial class DriveEngine(DriveNamespace ns, IDriveApi api, ContentCache content, DriveEngineOptions options)
{
    /// <summary>
    /// A folder's children. Loads page by page, readable while pages arrive. Kept current by the drive's change feed when it covers
    /// it (FolderId known, requested after the feed's baseline), else read again in the background after ListingTtl.
    /// </summary>
    sealed class Listing(DateTimeOffset requestedAt)
    {
        readonly Lock gate = new();
        readonly List<DriveItemInfo> arrived = [];
        readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshing;

        public ConcurrentDictionary<string, DriveItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public DateTimeOffset RequestedAt { get; } = requestedAt;
        public DateTimeOffset FetchedAt { get; private set; }
        public DateTimeOffset LastUsed { get; set; } = requestedAt;
        public DateTimeOffset RetryRefreshAt { get; set; }
        public string? FolderId { get; set; }
        public bool IsLoaded => loaded.Task.IsCompletedSuccessfully;
        public bool IsLoading => !loaded.Task.IsCompleted;

        public static Listing Loaded(IEnumerable<DriveItemInfo> items, DateTimeOffset requestedAt, DateTimeOffset fetchedAt, string? folderId)
        {
            var listing = new Listing(requestedAt) { FolderId = folderId };
            listing.AddPage(items.ToList());
            listing.Finish(fetchedAt);
            return listing;
        }

        public void AddPage(IReadOnlyList<DriveItemInfo> page)
        {
            lock (gate)
            {
                foreach (var item in page)
                {
                    Items[item.Name] = item;
                }
                arrived.AddRange(page);
                Signal();
            }
        }

        public void Finish(DateTimeOffset at)
        {
            lock (gate)
            {
                FetchedAt = at;
                loaded.TrySetResult();
                Signal();
            }
        }

        /// <summary>Stale from now on: the next open answers from it and reads it again in the background.</summary>
        public void Expire() => FetchedAt = DateTimeOffset.MinValue;

        public void Fail(Exception e)
        {
            lock (gate)
            {
                loaded.TrySetException(e);
                Signal();
            }
        }

        /// <summary>Items that arrived after the first <paramref name="from"/>; waits for the next page when none are new. Done once all pages are in.</summary>
        public async Task<(IReadOnlyList<DriveItemInfo> Items, bool Done)> NextAsync(int from, CancellationToken ct)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (loaded.Task.IsFaulted)
                    {
                        loaded.Task.GetAwaiter().GetResult();
                    }
                    if (arrived.Count > from || loaded.Task.IsCompleted)
                    {
                        return (arrived.GetRange(from, arrived.Count - from), loaded.Task.IsCompleted);
                    }
                    wait = more.Task;
                }
                await wait.WaitAsync(ct);
            }
        }

        public bool TryStartRefresh() => Interlocked.CompareExchange(ref refreshing, 1, 0) == 0;

        public void EndRefresh() => Volatile.Write(ref refreshing, 0);

        void Signal()
        {
            var previous = more;
            more = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    readonly ConcurrentDictionary<string, Listing> listings = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, (DriveItemInfo? Item, DateTimeOffset At)> lookups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every cached folder listing and path lookup counts as stale (a refresh asked for by the user). Nothing is read now: a folder is
    /// read again when it is next opened, and what changed is reported then.
    /// </summary>
    public void ExpireListings()
    {
        foreach (var listing in listings.Values.Where(l => l.IsLoaded))
        {
            listing.Expire();
        }
        lookups.Clear();
    }
    readonly ConcurrentDictionary<string, LocalState> overlay = new(StringComparer.OrdinalIgnoreCase);
    readonly DateTimeOffset startedAt = options.Clock();

    DateTimeOffset Now => options.Clock();

    internal static string Key(string driveId, string drivePath) => $"{driveId}|{drivePath}";

    public DriveNamespace Namespace => ns;

    public async Task<FsEntry?> GetEntryAsync(string path, CancellationToken ct)
    {
        var resolved = ns.Resolve(path);
        if (resolved is null)
        {
            return null;
        }
        if (resolved.Folder is not null)
        {
            return FolderEntry(resolved.Path, resolved.Folder);
        }

        var driveId = await resolved.Drive!.GetDriveIdAsync(ct);
        Touch(driveId);
        switch (overlay.GetValueOrDefault(Key(driveId, resolved.DrivePath)))
        {
            case PendingFile pending:
                return PendingEntry(resolved.Path, resolved.Drive, pending);
            case GoneItem:
                return null;
            case AliasItem alias:
                return ItemEntry(resolved.Path, resolved.Drive, driveId, alias.DrivePath, alias.Item with { Name = alias.Name });
        }
        var item = await LookupAsync(driveId, resolved.DrivePath, ct);
        return item is null ? null : ItemEntry(resolved.Path, resolved.Drive, driveId, resolved.DrivePath, item);
    }

    /// <summary>The Graph drive an entry belongs to, resolved if needed; null above the libraries.</summary>
    public async Task<string?> DriveIdOfAsync(FsEntry entry, CancellationToken ct) => entry.Drive is { } drive ? await drive.GetDriveIdAsync(ct) : null;

    /// <summary>All of a folder's entries.</summary>
    public async Task<IReadOnlyList<FsEntry>> ListAsync(FsEntry directory, CancellationToken ct)
    {
        var all = new List<FsEntry>();
        await foreach (var batch in ListStreamAsync(directory, ct))
        {
            all.AddRange(batch);
        }
        return all;
    }

    /// <summary>
    /// A folder's entries in batches: all at once when cached (even if stale; a refresh then runs in the background and reports
    /// what changed), else page by page as Graph returns them, so a huge folder starts showing after its first page.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListStreamAsync(FsEntry directory, [EnumeratorCancellation] CancellationToken ct)
    {
        if (directory.Folder is { Drive: null } folder)
        {
            yield return folder.Children.Values.Select(child => FolderEntry(Join(directory.Path, child.Name), child)).ToList();
            yield break;
        }

        var drive = directory.Drive!;
        var driveId = await drive.GetDriveIdAsync(ct);
        var listing = OpenListing(driveId, directory.DrivePath);
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<FsEntry>();
        foreach (var local in overlay.Values.Where(l => l.DriveId == driveId && ParentOf(l.DrivePath).Equals(directory.DrivePath, StringComparison.OrdinalIgnoreCase)))
        {
            var path = Join(directory.Path, local.Name);
            if (local is PendingFile pending && shown.Add(local.Name))
            {
                batch.Add(PendingEntry(path, drive, pending));
            }
            else if (local is AliasItem alias && shown.Add(local.Name))
            {
                batch.Add(ItemEntry(path, drive, driveId, alias.DrivePath, alias.Item with { Name = alias.Name }));
            }
        }

        IEnumerable<FsEntry> Entries(IEnumerable<DriveItemInfo> items) => items
            .Where(item => !overlay.ContainsKey(Key(driveId, JoinDrivePath(directory.DrivePath, item.Name))) && shown.Add(item.Name))
            .Select(item => ItemEntry(Join(directory.Path, item.Name), drive, driveId, JoinDrivePath(directory.DrivePath, item.Name), item));

        if (listing.IsLoaded)
        {
            batch.AddRange(Entries(listing.Items.Values));
            yield return batch;
            yield break;
        }
        for (var from = 0; ;)
        {
            var (items, done) = await listing.NextAsync(from, ct);
            from += items.Count;
            batch.AddRange(Entries(items));
            if (batch.Count > 0 || done)
            {
                yield return batch;
                batch = [];
            }
            if (done)
            {
                yield break;
            }
        }
    }

    /// <summary>The file's current content: its staging file while it is being changed, else the cached or streaming download. Dispose when done.</summary>
    public async Task<IContentSource> OpenContentAsync(FsEntry file, CancellationToken ct)
    {
        if (StagingPathOf(file) is { } staging)
        {
            return new FileContent(staging);
        }
        var driveId = await file.Drive!.GetDriveIdAsync(ct);
        NoteRead(driveId, file);
        return content.Open(driveId, file.ItemId!, file.ContentTag, file.Size, current => ContentChanged(driveId, current));
    }

    public string? StagingPathOf(FsEntry file) =>
        file.DriveId is not null && overlay.GetValueOrDefault(Key(file.DriveId, file.DrivePath)) is PendingFile pending ? pending.StagingPath : null;

    async Task<DriveItemInfo?> LookupAsync(string driveId, string drivePath, CancellationToken ct)
    {
        if (TryGetFreshListing(driveId, ParentOf(drivePath), out var parent))
        {
            return parent.Items.GetValueOrDefault(NameOf(drivePath));
        }

        var key = Key(driveId, drivePath);
        if (lookups.TryGetValue(key, out var known) && Now - known.At < (known.Item is null ? options.NotFoundTtl : options.LookupTtl))
        {
            return known.Item;
        }

        var item = await api.GetItemAsync(driveId, drivePath, ct);
        lookups[key] = (item, Now);
        return item;
    }

    bool TryGetFreshListing(string driveId, string folderPath, out Listing listing) =>
        listings.TryGetValue(Key(driveId, folderPath), out listing!) && IsFresh(driveId, listing);

    bool IsFresh(string driveId, Listing listing) =>
        listing.IsLoaded && Now - listing.FetchedAt < (Maintained(driveId, listing) ? options.MaintainedListingMaxAge : options.ListingTtl);

    /// <summary>
    /// The listing to answer from: the cached one (fresh, still loading, or stale with a refresh started), else a new one that starts
    /// loading now. Everyone asking for the same folder meanwhile shares it.
    /// </summary>
    Listing OpenListing(string driveId, string folderPath)
    {
        Touch(driveId);
        var key = Key(driveId, folderPath);
        while (true)
        {
            var current = listings.GetValueOrDefault(key);
            if (current is not null && (current.IsLoading || current.IsLoaded))
            {
                current.LastUsed = Now;
                NoteWalker(driveId, folderPath, startedLoad: false);
                if (current.IsLoaded && !IsFresh(driveId, current) && Now >= current.RetryRefreshAt && current.TryStartRefresh())
                {
                    _ = RefreshAsync(driveId, folderPath, key, current);
                }
                return current;
            }
            var next = new Listing(Now);
            if (current is null ? listings.TryAdd(key, next) : listings.TryUpdate(key, next, current))
            {
                NoteWalker(driveId, folderPath, startedLoad: true);
                _ = LoadAsync(driveId, folderPath, key, next);
                return next;
            }
        }
    }

    async Task LoadAsync(string driveId, string folderPath, string key, Listing listing)
    {
        try
        {
            var folderId = FolderIdOrNullAsync(driveId, folderPath);
            await foreach (var page in api.ListChildrenAsync(driveId, folderPath, CancellationToken.None))
            {
                listing.AddPage(page);
            }
            listing.FolderId = await folderId;
            listing.Finish(Now);
            PrefetchChildren(driveId, folderPath, listing);
        }
        catch (Exception e)
        {
            listings.TryRemove(new KeyValuePair<string, Listing>(key, listing));
            listing.Fail(e);
        }
    }

    /// <summary>Reads a stale listing again while it keeps answering; swaps in the result and reports what changed.</summary>
    async Task RefreshAsync(string driveId, string folderPath, string key, Listing stale)
    {
        RequestPriority.MarkBackground();
        try
        {
            var requestedAt = Now;
            var folderId = FolderIdOrNullAsync(driveId, folderPath);
            var items = new List<DriveItemInfo>();
            await foreach (var page in api.ListChildrenAsync(driveId, folderPath, CancellationToken.None))
            {
                items.AddRange(page);
            }
            var fresh = Listing.Loaded(items, requestedAt, Now, await folderId ?? stale.FolderId);
            fresh.LastUsed = stale.LastUsed;
            if (listings.TryUpdate(key, fresh, stale))
            {
                ReportDifferences(driveId, folderPath, stale, fresh);
            }
        }
        catch (Exception e)
        {
            stale.RetryRefreshAt = Now + options.RetryInterval;
            Log($"refresh of {folderPath} failed, showing the cached listing: {e.Message}");
        }
        finally
        {
            stale.EndRefresh();
        }
    }

    async Task<string?> FolderIdOrNullAsync(string driveId, string folderPath)
    {
        try
        {
            return folderPath.Length == 0 ? await FolderIdAsync(driveId, "", CancellationToken.None) : (await LookupAsync(driveId, folderPath, CancellationToken.None))?.Id;
        }
        catch (Exception e) when (e is RemoteException or HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Records a known item in the caches, so the next lookup or listing needs no call.</summary>
    void Upsert(string driveId, string drivePath, DriveItemInfo item)
    {
        if (listings.TryGetValue(Key(driveId, ParentOf(drivePath)), out var listing) && listing.IsLoaded)
        {
            listing.Items.TryRemove(NameOf(drivePath), out _);
            listing.Items[item.Name] = item;
        }
        lookups[Key(driveId, drivePath)] = (item, Now);
    }

    /// <summary>Records that nothing exists at the path any more.</summary>
    void Forget(string driveId, string drivePath)
    {
        if (listings.TryGetValue(Key(driveId, ParentOf(drivePath)), out var listing) && listing.IsLoaded)
        {
            listing.Items.TryRemove(NameOf(drivePath), out _);
        }
        lookups[Key(driveId, drivePath)] = (null, Now);
    }

    /// <summary>Drops what is cached at and below a path, so it is read again.</summary>
    void Invalidate(string driveId, string drivePath)
    {
        DropBelow(driveId, drivePath);
        if (listings.TryGetValue(Key(driveId, ParentOf(drivePath)), out var listing) && listing.IsLoaded)
        {
            listing.Items.TryRemove(NameOf(drivePath), out _);
            listings.TryRemove(Key(driveId, ParentOf(drivePath)), out _);
        }
    }

    /// <summary>Removes cached listings and lookups at and below a path.</summary>
    void DropBelow(string driveId, string drivePath)
    {
        var key = Key(driveId, drivePath);
        foreach (var stale in listings.Keys.Concat(lookups.Keys).Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "/", StringComparison.OrdinalIgnoreCase)))
        {
            listings.TryRemove(stale, out _);
            lookups.TryRemove(stale, out _);
        }
    }

    FsEntry FolderEntry(string path, VirtualFolder folder) => new()
    {
        Path = path,
        Name = folder.Name,
        IsDirectory = true,
        Created = startedAt,
        Modified = startedAt,
        ReadOnly = folder.Drive?.ReadOnly ?? true,
        Folder = folder,
        Drive = folder.Drive,
    };

    /// <summary>Read-only for this engine: the library is, or the database file policy makes the file so.</summary>
    bool IsReadOnly(DriveMount drive, string name, bool isFolder) =>
        drive.ReadOnly || (!isFolder && options.DatabaseFiles != DatabaseFilePolicy.Allow && DatabaseFiles.IsDatabase(name));

    bool IsBlocked(string name, bool isFolder) => !isFolder && options.DatabaseFiles == DatabaseFilePolicy.Block && DatabaseFiles.IsDatabase(name);

    FsEntry ItemEntry(string path, DriveMount drive, string driveId, string drivePath, DriveItemInfo item) => new()
    {
        Path = path,
        Name = item.Name,
        IsDirectory = item.IsFolder,
        Size = item.IsFolder ? 0 : ServedSize(driveId, item),
        Created = item.Created,
        Modified = item.Modified,
        ReadOnly = IsReadOnly(drive, item.Name, item.IsFolder),
        Blocked = IsBlocked(item.Name, item.IsFolder),
        ItemId = item.Id,
        ContentTag = item.CTag ?? item.ETag,
        Drive = drive,
        DriveId = driveId,
        DrivePath = drivePath,
    };

    FsEntry PendingEntry(string path, DriveMount drive, PendingFile pending) => new()
    {
        Path = path,
        Name = pending.Name,
        Size = new FileInfo(pending.StagingPath).Length,
        Created = pending.Base?.Created ?? pending.Modified,
        Modified = pending.Modified,
        ReadOnly = IsReadOnly(drive, pending.Name, false),
        ItemId = pending.Base?.Id,
        StagingPath = pending.StagingPath,
        Drive = drive,
        DriveId = pending.DriveId,
        DrivePath = pending.DrivePath,
    };

    static string Join(string path, string name) => path == "\\" ? "\\" + name : path + "\\" + name;

    internal static string JoinDrivePath(string path, string name) => path.Length == 0 ? name : path + "/" + name;

    internal static string ParentOf(string drivePath) => drivePath.LastIndexOf('/') is var split and >= 0 ? drivePath[..split] : "";

    internal static string NameOf(string drivePath) => drivePath[(drivePath.LastIndexOf('/') + 1)..];
}
