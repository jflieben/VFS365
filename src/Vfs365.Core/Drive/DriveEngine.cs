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

    /// <summary>
    /// A drive used within HotWindow is read this often, push or not (zero: never); its changes show sooner than push brings them.
    /// With push up, each read since this engine last wrote to the drive doubles the wait, up to 8 times this (push brings the
    /// changes). Without push, each read in a row that brought no change doubles it, up to 4 times this.
    /// </summary>
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
    public TimeSpan MaintainedListingMaxAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Folder listings kept in memory, counted as their items plus one per listing. Beyond it the least recently used go (never
    /// one that is loading or was used in the last minutes); they are read again when opened.
    /// </summary>
    public int MaxCachedItems { get; init; } = 250_000;

    /// <summary>
    /// A change feed read, a listing page or a folder lookup for a listing is given up after this long without an answer and counts
    /// as failed. A backstop: the HTTP client has shorter timeouts of its own.
    /// </summary>
    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromMinutes(3);

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Randomness (0 to 1) for jitter: back-offs, catch-up reads after push reconnects and network returns.</summary>
    public Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

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

    /// <summary>Microsoft 365 refused a delete (locked, or no permission to delete); the file shows again. Detail is the reason.</summary>
    NotDeleted,
}

/// <summary>Path is the volume path (null when its library isn't mounted any more).</summary>
public sealed record EngineNotice(NoticeKind Kind, string Name, string? Path, string? Detail);

/// <summary>Front-end-neutral file system over Graph: resolves paths, lists folders, fetches and saves content, with caching.</summary>
public sealed partial class DriveEngine(DriveNamespace ns, IDriveApi api, ContentCache content, DriveEngineOptions options)
{
    /// <summary>
    /// A folder's children. Loads page by page, readable while pages arrive. Kept current by the drive's change feed when it covers
    /// it (<see cref="Maintained"/>), else read again in the background after ListingTtl. A load's requests run at Priority's level:
    /// a load started in the background (ahead of a walk, for a burst of lookups) is promoted when a user waits for it.
    /// </summary>
    sealed class Listing(DateTimeOffset requestedAt, PriorityHolder? priority = null)
    {
        public PriorityHolder Priority { get; } = priority ?? new PriorityHolder(RequestPriority.IsBackground);

        readonly Lock gate = new();
        readonly List<DriveItemInfo> arrived = [];
        readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshing, version, sortedVersion = -1;
        DriveItemInfo[] sortedFiles = [];

        /// <summary>Children by name. Change them only through <see cref="Set"/> and <see cref="Remove"/>, which keep <see cref="ById"/>.</summary>
        public ConcurrentDictionary<string, DriveItemInfo> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Children's names by item ID.</summary>
        public ConcurrentDictionary<string, string> ById { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset RequestedAt { get; } = requestedAt;
        public DateTimeOffset FetchedAt { get; private set; }
        public DateTimeOffset LastUsed { get; set; } = requestedAt;
        public DateTimeOffset RetryRefreshAt { get; set; }
        public string? FolderId { get; set; }

        /// <summary>The folder's parent's ID, where known: a change that puts the folder elsewhere drops the listing.</summary>
        public string? ParentId { get; set; }

        public bool IsLoaded => loaded.Task.IsCompletedSuccessfully;
        public bool IsLoading => !loaded.Task.IsCompleted;

        /// <summary>Marked stale (a refresh asked for, or changes may be missing): read again before it answers lookups.</summary>
        public bool IsExpired => FetchedAt == DateTimeOffset.MinValue;

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
                    SetLocked(item);
                }
                arrived.AddRange(page);
                Signal();
            }
        }

        /// <summary>Adds or replaces a child; an item ID has one name.</summary>
        public void Set(DriveItemInfo item)
        {
            lock (gate)
            {
                SetLocked(item);
            }
        }

        void SetLocked(DriveItemInfo item)
        {
            if (Items.TryGetValue(item.Name, out var other) && other.Id != item.Id)
            {
                ById.TryRemove(new KeyValuePair<string, string>(other.Id, other.Name));
            }
            if (ById.TryGetValue(item.Id, out var name) && !name.Equals(item.Name, StringComparison.OrdinalIgnoreCase))
            {
                Items.TryRemove(name, out _);
            }
            Items[item.Name] = item;
            ById[item.Id] = item.Name;
            version++;
        }

        public bool Remove(string name, out DriveItemInfo? removed)
        {
            lock (gate)
            {
                if (!Items.TryRemove(name, out removed))
                {
                    return false;
                }
                ById.TryRemove(new KeyValuePair<string, string>(removed.Id, removed.Name));
                version++;
                return true;
            }
        }

        /// <summary>The files by name, sorted once per change of the listing.</summary>
        public DriveItemInfo[] SortedFiles()
        {
            lock (gate)
            {
                if (sortedVersion != version)
                {
                    sortedFiles = [.. Items.Values.Where(i => !i.IsFolder).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)];
                    sortedVersion = version;
                }
                return sortedFiles;
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
    long metadataVersion;

    /// <summary>
    /// Changes whenever cached listings change (loaded, refreshed, changed by the feed or by writes, dropped). The host saves the
    /// metadata snapshot only when it differs from the version it saved last.
    /// </summary>
    public long MetadataVersion => Interlocked.Read(ref metadataVersion);

    DateTimeOffset Now => options.Clock();

    internal static string Key(string driveId, string drivePath) => $"{driveId}|{drivePath}";

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
        Touch(driveId, engage: resolved.DrivePath.Contains('/')); // names at a library's top are probed for thumbnails and desktop.ini
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
        // Local state in this folder: shown instead of what the listing has under those names
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!overlay.IsEmpty)
        {
            foreach (var state in overlay.Values.Where(l => l.DriveId == driveId && ParentOf(l.DrivePath).Equals(directory.DrivePath, StringComparison.OrdinalIgnoreCase)))
            {
                local.Add(state.Name);
                var path = Join(directory.Path, state.Name);
                if (state is PendingFile pending && shown.Add(state.Name))
                {
                    batch.Add(PendingEntry(path, drive, pending));
                }
                else if (state is AliasItem alias && shown.Add(state.Name))
                {
                    batch.Add(ItemEntry(path, drive, driveId, alias.DrivePath, alias.Item with { Name = alias.Name }));
                }
            }
        }

        IEnumerable<FsEntry> Entries(IEnumerable<DriveItemInfo> items) => items
            .Where(item => (local.Count == 0 || !local.Contains(item.Name)) && shown.Add(item.Name))
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

    /// <summary>
    /// The file's current content: its staging file while it is being changed, else the cached or streaming download (of the item a
    /// staged entry was uploaded into, see <see cref="Follow"/>). Dispose when done.
    /// </summary>
    public async Task<IContentSource> OpenContentAsync(FsEntry file, CancellationToken ct)
    {
        file = Follow(file);
        if (file.StagingPath is { } staging)
        {
            return new FileContent(staging);
        }
        var driveId = await file.Drive!.GetDriveIdAsync(ct);
        Touch(driveId);
        NoteRead(driveId, file);
        return content.Open(driveId, file.ItemId!, file.ContentTag, file.Size, current => ContentChanged(driveId, current));
    }

    async Task<DriveItemInfo?> LookupAsync(string driveId, string drivePath, CancellationToken ct)
    {
        Listing? stale = null;
        if (drivePath.Length > 0)
        {
            var folder = ParentOf(drivePath);
            var currency = CurrencyOf(driveId, folder, out var parent);
            stale = parent is { IsLoaded: true } ? parent : null;
            if (currency == Currency.FeedBehind)
            {
                // Kept by a feed not read lately (idle, asleep, just started): what changed meanwhile comes first, briefly
                await CatchUpAsync(driveId);
                currency = CurrencyOf(driveId, folder, out parent, caughtUp: true);
            }
            if (currency == Currency.Current)
            {
                parent.LastUsed = Now;
                return parent.Items.GetValueOrDefault(NameOf(drivePath));
            }
            if (parent is { IsLoaded: true } && Now >= parent.RetryRefreshAt && parent.TryStartRefresh())
            {
                _ = RefreshAsync(driveId, folder, Key(driveId, folder), parent); // read again once, rather than a lookup per file meanwhile
            }
        }

        var key = Key(driveId, drivePath);
        if (lookups.TryGetValue(key, out var known) && Now - known.At < (known.Item is null ? options.NotFoundTtl : options.LookupTtl))
        {
            return known.Item;
        }

        if (drivePath.Length > 0)
        {
            NoteLookup(driveId, ParentOf(drivePath));
        }
        DriveItemInfo? item;
        try
        {
            item = await api.GetItemAsync(driveId, drivePath, ct);
        }
        catch (Exception e) when (stale is not null && !ct.IsCancellationRequested && IsTransient(e, readOnlyCounts: false))
        {
            // Offline or Microsoft 365 unreachable: the last known listing answers (cached files keep opening), until it can be read again
            return stale.Items.GetValueOrDefault(NameOf(drivePath));
        }
        lookups[key] = (item, Now);
        return item;
    }

    enum Currency { No, Current, FeedBehind }

    /// <summary>
    /// Whether a cached listing answers lookups. Current: fresh, or kept current by the change feed (however old, unless marked stale)
    /// and the feed was read lately or <paramref name="caughtUp"/>. FeedBehind: maintained, but the feed wasn't read lately. Old
    /// maintained listings are still read again when opened, as a safety net.
    /// </summary>
    Currency CurrencyOf(string driveId, string folderPath, out Listing listing, bool caughtUp = false)
    {
        if (!listings.TryGetValue(Key(driveId, folderPath), out listing!) || !listing.IsLoaded)
        {
            return Currency.No;
        }
        if (!Maintained(driveId, folderPath, listing))
        {
            return Now - listing.FetchedAt < options.ListingTtl ? Currency.Current : Currency.No;
        }
        return listing.IsExpired ? Currency.No : caughtUp || FeedCurrent(driveId) ? Currency.Current : Currency.FeedBehind;
    }

    bool TryGetCurrentListing(string driveId, string folderPath, out Listing listing) => CurrencyOf(driveId, folderPath, out listing) != Currency.No;

    bool IsFresh(string driveId, string folderPath, Listing listing) =>
        listing.IsLoaded && Now - listing.FetchedAt < (Maintained(driveId, folderPath, listing) ? options.MaintainedListingMaxAge : options.ListingTtl);

    const int LookupBurst = 5;

    /// <summary>Lookups by path per folder lately: an app opening many files of a folder never listed gets the folder listed instead.</summary>
    readonly ConcurrentDictionary<string, (int Count, DateTimeOffset Since)> lookupBursts = new(StringComparer.OrdinalIgnoreCase);

    void NoteLookup(string driveId, string folderPath)
    {
        var key = Key(driveId, folderPath);
        var burst = lookupBursts.AddOrUpdate(key, _ => (1, Now), (_, b) => Now - b.Since > options.WalkWindow ? (1, Now) : (b.Count + 1, b.Since));
        if (burst.Count < LookupBurst || listings.ContainsKey(key))
        {
            return;
        }
        lookupBursts.TryRemove(key, out _);
        var listing = new Listing(Now, new PriorityHolder());
        if (listings.TryAdd(key, listing))
        {
            Log($"{LookupBurst} files looked up by path in '{folderPath}' on drive {driveId} within {options.WalkWindow.TotalSeconds:0} s: listing the folder");
            _ = LoadInBackgroundAsync(driveId, folderPath, key, listing);
        }
    }

    async Task LoadInBackgroundAsync(string driveId, string folderPath, string key, Listing listing)
    {
        await Task.Yield();
        await LoadAsync(driveId, folderPath, key, listing);
    }

    /// <summary>
    /// The listing to answer from: the cached one (fresh, still loading, or stale with a refresh started), else a new one that starts
    /// loading now. Everyone asking for the same folder meanwhile shares it.
    /// </summary>
    Listing OpenListing(string driveId, string folderPath)
    {
        var key = Key(driveId, folderPath);
        // A library's top listed once (Explorer drawing a thumbnail of it) starts no change feed; a second look or anything deeper does
        Touch(driveId, engage: folderPath.Length > 0 || (listings.GetValueOrDefault(key) is { } earlier && Now - earlier.RequestedAt >= ShallowGrace));
        while (true)
        {
            var current = listings.GetValueOrDefault(key);
            if (current is not null && (current.IsLoading || current.IsLoaded))
            {
                current.LastUsed = Now;
                if (current.IsLoading && !RequestPriority.IsBackground)
                {
                    current.Priority.Promote(); // loading ahead or for lookups, and now someone waits for it
                }
                NoteWalker(driveId, folderPath, startedLoad: false);
                if (current.IsLoaded && !IsFresh(driveId, folderPath, current) && Now >= current.RetryRefreshAt && current.TryStartRefresh())
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

    static readonly TimeSpan ShallowGrace = TimeSpan.FromSeconds(10);

    async Task LoadAsync(string driveId, string folderPath, string key, Listing listing)
    {
        RequestPriority.Use(listing.Priority);
        try
        {
            var folder = FolderOrNullAsync(driveId, folderPath);
            await foreach (var page in PagesAsync(driveId, folderPath))
            {
                listing.AddPage(page);
            }
            (listing.FolderId, listing.ParentId) = await folder;
            var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
            lock (feed.ApplyGate)
            {
                Interlocked.Increment(ref metadataVersion);
                listing.Finish(Now);
                Replay(driveId, folderPath, listing);
            }
            unlisted.TryRemove(key, out _);
            PrefetchChildren(driveId, folderPath, listing);
            ListAncestors(driveId, folderPath);
        }
        catch (Exception e)
        {
            unlisted[key] = Now;
            listings.TryRemove(new KeyValuePair<string, Listing>(key, listing));
            listing.Fail(e);
        }
    }

    /// <summary>Folders whose listing failed lately (no access to it, for one): not listed again as a folder above another for a while.</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> unlisted = new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan UnlistedRetry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Lists the folders above a listed one that aren't cached, in the background (an app opening files by path lists only the folder
    /// it opens): only with them can the change feed see a rename or move above it, and keep it current without reading it again.
    /// </summary>
    void ListAncestors(string driveId, string folderPath)
    {
        for (var path = ParentOf(folderPath); path.Length > 0; path = ParentOf(path))
        {
            var key = Key(driveId, path);
            if (listings.ContainsKey(key) || (unlisted.TryGetValue(key, out var failed) && Now - failed < UnlistedRetry))
            {
                continue;
            }
            var listing = new Listing(Now, new PriorityHolder());
            if (listings.TryAdd(key, listing))
            {
                _ = LoadInBackgroundAsync(driveId, path, key, listing);
            }
        }
    }

    /// <summary>
    /// Reads a stale listing again while it keeps answering; swaps in the result and reports what changed. A folder no longer found
    /// at its path (it, or a folder above it, was renamed, moved or deleted) loses its cached listings.
    /// </summary>
    async Task RefreshAsync(string driveId, string folderPath, string key, Listing stale)
    {
        RequestPriority.MarkBackground();
        try
        {
            var requestedAt = Now;
            // A folder the feed kept current is still the same folder (a move or rename of it or above it would have dropped the listing)
            var folder = Maintained(driveId, folderPath, stale)
                ? Task.FromResult<(string?, string?)>((stale.FolderId, stale.ParentId))
                : FolderOrNullAsync(driveId, folderPath);
            var items = new List<DriveItemInfo>();
            await foreach (var page in PagesAsync(driveId, folderPath))
            {
                items.AddRange(page);
            }
            var (folderId, parentId) = await folder;
            var fresh = Listing.Loaded(items, requestedAt, Now, folderId ?? stale.FolderId);
            fresh.ParentId = parentId ?? stale.ParentId;
            fresh.LastUsed = stale.LastUsed;
            var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
            lock (feed.ApplyGate)
            {
                if (!listings.TryUpdate(key, fresh, stale))
                {
                    return;
                }
                Replay(driveId, folderPath, fresh);
            }
            Interlocked.Increment(ref metadataVersion);
            ReportDifferences(driveId, folderPath, stale, fresh);
            ListAncestors(driveId, folderPath);
        }
        catch (RemoteException e) when (e.Error == RemoteError.NotFound)
        {
            var dropped = false;
            lock (feeds.GetOrAdd(driveId, _ => NewFeed()).ApplyGate)
            {
                if (listings.TryGetValue(key, out var cached) && cached == stale)
                {
                    DropBelow(driveId, folderPath);
                    dropped = true;
                }
            }
            if (dropped)
            {
                Log($"{folderPath} is no longer there (renamed, moved or deleted, or a folder above it was); its cached listing is dropped");
                Report(driveId, folderPath, ChangeKind.Removed, true);
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

    /// <summary>The folder's ID and its parent's ID, from cached listings where they answer, else from Graph; nulls when that fails.</summary>
    async Task<(string? Id, string? ParentId)> FolderOrNullAsync(string driveId, string folderPath)
    {
        try
        {
            if (folderPath.Length == 0)
            {
                return (await CallAsync(ct => FolderIdAsync(driveId, "", ct)), null);
            }
            var parentId = TryGetCurrentListing(driveId, ParentOf(folderPath), out var parent) ? parent.FolderId : null;
            var folder = await CallAsync(ct => LookupAsync(driveId, folderPath, ct));
            return (folder?.Id, folder?.ParentId ?? parentId);
        }
        catch (Exception e) when (e is RemoteException or HttpRequestException or OperationCanceledException or TimeoutException)
        {
            return (null, null);
        }
    }

    /// <summary>A folder's pages, each given up after CallTimeout without an answer (<see cref="TimeoutException"/>).</summary>
    async IAsyncEnumerable<IReadOnlyList<DriveItemInfo>> PagesAsync(string driveId, string folderPath)
    {
        using var deadline = new CancellationTokenSource();
        var pages = api.ListChildrenAsync(driveId, folderPath, deadline.Token).GetAsyncEnumerator(deadline.Token);
        Task<bool>? next = null;
        try
        {
            while (true)
            {
                deadline.CancelAfter(options.CallTimeout);
                next = pages.MoveNextAsync().AsTask();
                if (!await WithinAsync(next, deadline.Token))
                {
                    yield break;
                }
                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                yield return pages.Current;
            }
        }
        finally
        {
            // A page still being read can't be disposed; it ends with the cancelled token
            if (next is null || next.IsCompleted)
            {
                await pages.DisposeAsync();
            }
        }
    }

    /// <summary>A call given up after CallTimeout without an answer (<see cref="TimeoutException"/>), also when it ignores its token.</summary>
    async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call)
    {
        using var deadline = new CancellationTokenSource(options.CallTimeout);
        return await WithinAsync(call(deadline.Token), deadline.Token);
    }

    async Task<T> WithinAsync<T>(Task<T> call, CancellationToken deadline)
    {
        try
        {
            return await call.WaitAsync(deadline);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new TimeoutException($"No answer from Microsoft 365 within {options.CallTimeout.TotalSeconds:0} s");
        }
    }

    /// <summary>Records a known item in the caches, so the next lookup or listing needs no call. Also notes the local write.</summary>
    void Upsert(string driveId, string drivePath, DriveItemInfo item)
    {
        Local(driveId, drivePath, item);
        lookups[Key(driveId, drivePath)] = (item, Now);
        LocalWrite(driveId);
    }

    /// <summary>Records that nothing exists at the path any more.</summary>
    void Forget(string driveId, string drivePath)
    {
        Local(driveId, drivePath, null);
        lookups[Key(driveId, drivePath)] = (null, Now);
        LocalWrite(driveId);
    }

    /// <summary>
    /// A local write at a path (<paramref name="item"/> null: nothing there now). Applied to its folder's listing, and kept with the
    /// feed's recent changes, so a listing of that folder that is loading or being read again meanwhile gets it too.
    /// </summary>
    void Local(string driveId, string drivePath, DriveItemInfo? item)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        lock (feed.ApplyGate)
        {
            Record(feed, [new Recorded(Now, null, drivePath, item)]);
            if (listings.TryGetValue(Key(driveId, ParentOf(drivePath)), out var listing) && listing.IsLoaded)
            {
                ApplyLocal(listing, drivePath, item, replay: false);
            }
        }
    }

    /// <summary>A local write onto a listing. Replayed, it doesn't put back an older version than the listing has.</summary>
    static void ApplyLocal(Listing listing, string drivePath, DriveItemInfo? item, bool replay)
    {
        var name = NameOf(drivePath);
        if (replay && item is not null && listing.Items.TryGetValue(name, out var known) && known.Id == item.Id && IsOlder(item.ETag, known.ETag))
        {
            return;
        }
        listing.Remove(name, out _);
        if (item is not null)
        {
            listing.Set(item);
        }
    }

    /// <summary>Drops what is cached at and below a path, so it is read again.</summary>
    void Invalidate(string driveId, string drivePath)
    {
        DropBelow(driveId, drivePath);
        if (listings.TryGetValue(Key(driveId, ParentOf(drivePath)), out var listing) && listing.IsLoaded)
        {
            listing.Remove(NameOf(drivePath), out _);
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
        Interlocked.Increment(ref metadataVersion);
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

    /// <summary>
    /// An entry for staged content, bound to it: handles that keep it follow that content (<see cref="Follow"/>). Content that was
    /// uploaded meanwhile (its staging file became cached content) is the item it went into.
    /// </summary>
    FsEntry PendingEntry(string path, DriveMount drive, PendingFile pending)
    {
        long size;
        try
        {
            size = new FileInfo(pending.StagingPath).Length;
        }
        catch (FileNotFoundException) when (pending.Result is { } result)
        {
            return ItemEntry(VolumePath(pending.DriveId, pending.ResultPath!) ?? path, drive, pending.DriveId, pending.ResultPath!, result);
        }
        catch (FileNotFoundException)
        {
            size = 0; // discarded meanwhile: the entry is bound to it, so writes through it fail
        }
        var entry = new FsEntry
        {
            Path = path,
            Name = pending.Name,
            Size = size,
            Created = pending.Base?.Created ?? pending.Modified,
            Modified = pending.Modified,
            ReadOnly = IsReadOnly(drive, pending.Name, false),
            ItemId = pending.Base?.Id,
            StagingPath = pending.StagingPath,
            Drive = drive,
            DriveId = pending.DriveId,
            DrivePath = pending.DrivePath,
        };
        bindings.AddOrUpdate(entry, pending);
        return entry;
    }

    static string Join(string path, string name) => path == "\\" ? "\\" + name : path + "\\" + name;

    internal static string JoinDrivePath(string path, string name) => path.Length == 0 ? name : path + "/" + name;

    internal static string ParentOf(string drivePath) => drivePath.LastIndexOf('/') is var split and >= 0 ? drivePath[..split] : "";

    internal static string NameOf(string drivePath) => drivePath[(drivePath.LastIndexOf('/') + 1)..];
}
