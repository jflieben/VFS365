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

    /// <summary>
    /// A lookup with a last known answer (a stale listing, an expired lookup) waits this long for Microsoft 365 at most; then that
    /// answer serves and the call goes on in the background.
    /// </summary>
    public TimeSpan LookupDeadline { get; init; } = TimeSpan.FromSeconds(10);

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

/// <summary>
/// Path is the volume path (null when its library isn't mounted any more). For a conflict copy of a file changed in Microsoft 365, By
/// and At name who saved that version and when, when Graph said so.
/// </summary>
public sealed record EngineNotice(NoticeKind Kind, string Name, string? Path, string? Detail, string? By = null, DateTimeOffset? At = null);

/// <summary>Front-end-neutral file system over Graph: resolves paths, lists folders, fetches and saves content, with caching.</summary>
public sealed partial class DriveEngine(DriveNamespace ns, IDriveApi api, ContentCache content, DriveEngineOptions options)
{
    /// <summary>
    /// A folder's children. Loads page by page, readable while pages arrive. Kept current by the drive's change feed when it covers
    /// it (<see cref="Maintained"/>), else read again in the background after ListingTtl. A load's requests run at Priority's level:
    /// a load started in the background (ahead of a walk, for a burst of lookups) is promoted when a user waits for it.
    /// </summary>
    sealed class Listing(DateTimeOffset requestedAt, PriorityHolder? priority = null, int capacity = 0)
    {
        static long serials;

        public PriorityHolder Priority { get; } = priority ?? new PriorityHolder(RequestPriority.IsBackground);

        /// <summary>Unique per listing, for <see cref="MetadataVersion"/>.</summary>
        public long Serial { get; } = Interlocked.Increment(ref serials);

        readonly Lock gate = new();

        /// <summary>Items in the order pages brought them, for readers while it loads; let go once it is loaded.</summary>
        List<DriveItemInfo>? arrived = [];
        readonly TaskCompletionSource loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource more = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int refreshing, version, sortedVersion = -1;
        DriveItemInfo[] sortedFiles = [];
        EntryCache? entries;

        sealed record EntryCache(int Version, string Path, FsEntry[] Entries);

        /// <summary>
        /// Children by name. Change them only through <see cref="Set"/> and <see cref="Remove"/>, which keep <see cref="ById"/>. Writers
        /// hold the gate, so one lock per dictionary does (the default is one per core, kilobytes per listing).
        /// </summary>
        public ConcurrentDictionary<string, DriveItemInfo> Items { get; } = new(1, capacity > 0 ? capacity : 31, StringComparer.OrdinalIgnoreCase);

        /// <summary>Children's names by item ID.</summary>
        public ConcurrentDictionary<string, string> ById { get; } = new(1, capacity > 0 ? capacity : 31, StringComparer.Ordinal);

        /// <summary>Changes with every change of a child that the front end could see.</summary>
        public int Version => Volatile.Read(ref version);

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

        public static Listing Loaded(IReadOnlyCollection<DriveItemInfo> items, DateTimeOffset requestedAt, DateTimeOffset fetchedAt, string? folderId)
        {
            var listing = new Listing(requestedAt, capacity: Math.Max(items.Count, 1)) { FolderId = folderId };
            listing.AddPage(items);
            listing.Finish(fetchedAt);
            return listing;
        }

        public void AddPage(IEnumerable<DriveItemInfo> page)
        {
            lock (gate)
            {
                foreach (var item in page)
                {
                    SetLocked(item);
                    arrived?.Add(item);
                }
                Signal();
            }
        }

        /// <summary>
        /// Adds or replaces a child; an item ID has one name. <paramref name="quiet"/>: nothing the front end shows changed (a folder's
        /// eTag and size after a change below it), so <see cref="Version"/> stays.
        /// </summary>
        public void Set(DriveItemInfo item, bool quiet = false)
        {
            lock (gate)
            {
                SetLocked(item, quiet);
            }
        }

        void SetLocked(DriveItemInfo item, bool quiet = false)
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
            if (!quiet)
            {
                version++;
            }
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

        /// <summary>
        /// The front end's entries for the children, sorted by name, as <paramref name="build"/> makes them. With <paramref name="keep"/>
        /// they are kept until the listing changes, so scans of a big folder don't build them again.
        /// </summary>
        public FsEntry[] Entries(string path, Func<ICollection<DriveItemInfo>, FsEntry[]> build, bool keep)
        {
            int at;
            lock (gate)
            {
                if (entries is { } known && known.Version == version && known.Path == path)
                {
                    return known.Entries;
                }
                at = version;
            }
            var built = build(Items.Values);
            if (keep)
            {
                lock (gate)
                {
                    if (version == at)
                    {
                        entries = new EntryCache(at, path, built);
                    }
                }
            }
            return built;
        }

        public bool HasEntries => Volatile.Read(ref entries) is not null;

        public void DropEntries()
        {
            lock (gate)
            {
                entries = null;
            }
        }

        /// <summary>Loaded as of <paramref name="at"/> (MinValue: loaded but stale, see <see cref="IsExpired"/>).</summary>
        public void Finish(DateTimeOffset at)
        {
            lock (gate)
            {
                FetchedAt = at;
                arrived = null;
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

        /// <summary>
        /// Items that arrived after the first <paramref name="from"/>; waits for the next page when none are new. Done once all pages are
        /// in; then it is every item (the arrival order is let go), and the reader leaves out what it has shown.
        /// </summary>
        public async Task<(ICollection<DriveItemInfo> Items, bool Done)> NextAsync(int from, CancellationToken ct)
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
                    if (arrived is null)
                    {
                        return (Items.Values, true);
                    }
                    if (arrived.Count > from)
                    {
                        return (arrived.GetRange(from, arrived.Count - from), false);
                    }
                    wait = more.Task;
                }
                await wait.WaitAsync(ct);
            }
        }

        long shownTicks;

        /// <summary>When the front end first opened it in this run: its request, or for one restored from a snapshot the first open.</summary>
        public DateTimeOffset FirstShown(DateTimeOffset now)
        {
            if (RequestedAt != DateTimeOffset.MinValue)
            {
                return RequestedAt;
            }
            Interlocked.CompareExchange(ref shownTicks, now.UtcTicks, 0);
            return new DateTimeOffset(Interlocked.Read(ref shownTicks), TimeSpan.Zero);
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
        var key = Key(driveId, resolved.DrivePath);
        while (true)
        {
            switch (overlay.GetValueOrDefault(key))
            {
                case PendingFile pending:
                    if (LivePendingEntry(resolved.Path, resolved.Drive, pending) is { } staged)
                    {
                        return staged;
                    }
                    continue; // discarded meanwhile (deleted, replaced): what is there now
                case GoneItem:
                    return null;
                case AliasItem alias:
                    return ItemEntry(resolved.Path, resolved.Drive, driveId, alias.DrivePath, alias.Item with { Name = alias.Name });
            }
            break;
        }
        var item = await LookupAsync(driveId, resolved.DrivePath, ct);
        return item is null ? null : ItemEntry(resolved.Path, resolved.Drive, driveId, resolved.DrivePath, item);
    }

    /// <summary>The Graph drive an entry belongs to, resolved if needed; null above the libraries.</summary>
    public async Task<string?> DriveIdOfAsync(FsEntry entry, CancellationToken ct) => entry.Drive is { } drive ? await drive.GetDriveIdAsync(ct) : null;

    /// <summary>All of a folder's entries, sorted by name (case-insensitive ordinal). Don't change the list: it may be shared.</summary>
    public async Task<IReadOnlyList<FsEntry>> ListAsync(FsEntry directory, CancellationToken ct)
    {
        var batches = new List<IReadOnlyList<FsEntry>>();
        await foreach (var batch in ListStreamAsync(directory, ct))
        {
            batches.Add(batch);
        }
        // One array is a whole sorted folder (cached or virtual); pages of a loading listing come as lists, in page order
        return batches is [FsEntry[] whole] ? whole : Sorted([.. batches.SelectMany(b => b)]);
    }

    /// <summary>Listings this big keep their entries between scans (smaller ones are cheap to build again).</summary>
    const int KeptEntriesFrom = 500;

    static readonly Comparison<FsEntry> ByName = (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);

    static FsEntry[] Sorted(FsEntry[] entries)
    {
        Array.Sort(entries, ByName);
        return entries;
    }

    /// <summary>
    /// A folder's entries in batches: all at once when cached (even if stale; a refresh then runs in the background and reports
    /// what changed), else page by page as Graph returns them, so a huge folder starts showing after its first page. The single batch
    /// of a cached folder is sorted by name (and kept until the listing changes); pages of a loading one are in page order.
    /// </summary>
    public async IAsyncEnumerable<IReadOnlyList<FsEntry>> ListStreamAsync(FsEntry directory, [EnumeratorCancellation] CancellationToken ct)
    {
        if (directory.Folder is { Drive: null } folder)
        {
            yield return Sorted([.. folder.Children.Values.Select(child => FolderEntry(Join(directory.Path, child.Name), child))]);
            yield break;
        }

        var drive = directory.Drive!;
        var driveId = await drive.GetDriveIdAsync(ct);
        var listing = OpenListing(driveId, directory.DrivePath);
        // Local state in this folder: shown instead of what the listing has under those names
        var local = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var localEntries = new List<FsEntry>();
        if (!overlay.IsEmpty)
        {
            foreach (var first in overlay.Values)
            {
                if (first.DriveId != driveId || !ParentOf(first.DrivePath).Equals(directory.DrivePath, StringComparison.OrdinalIgnoreCase) || !local.Add(first.Name))
                {
                    continue;
                }
                var path = Join(directory.Path, first.Name);
                for (LocalState? state = first; state is not null;)
                {
                    if (state is PendingFile pending)
                    {
                        if (LivePendingEntry(path, drive, pending) is { } staged)
                        {
                            localEntries.Add(staged);
                            break;
                        }
                        // Discarded meanwhile (deleted, replaced): what is at its name now, else what the listing has
                        state = overlay.GetValueOrDefault(pending.Key);
                        if (state is null)
                        {
                            local.Remove(first.Name);
                        }
                        continue;
                    }
                    if (state is AliasItem alias)
                    {
                        localEntries.Add(ItemEntry(path, drive, driveId, alias.DrivePath, alias.Item with { Name = alias.Name }));
                    }
                    break;
                }
            }
        }

        FsEntry Entry(DriveItemInfo item) => ItemEntry(Join(directory.Path, item.Name), drive, driveId, JoinDrivePath(directory.DrivePath, item.Name), item);

        if (listing.IsLoaded)
        {
            var entries = listing.Entries(directory.Path, items =>
            {
                var built = new FsEntry[items.Count];
                var i = 0;
                foreach (var item in items)
                {
                    built[i++] = Entry(item);
                }
                return Sorted(i == built.Length ? built : built[..i]);
            }, keep: listing.Items.Count >= KeptEntriesFrom);
            yield return local.Count == 0 ? entries : Sorted([.. entries.Where(e => !local.Contains(e.Name)), .. localEntries]);
            yield break;
        }
        var shown = new HashSet<string>(local, StringComparer.OrdinalIgnoreCase);
        List<FsEntry> batch = localEntries;
        for (var from = 0; ;)
        {
            var (items, done) = await listing.NextAsync(from, ct);
            from += items.Count;
            foreach (var item in items)
            {
                if (shown.Add(item.Name))
                {
                    batch.Add(Entry(item));
                }
            }
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
            try
            {
                return new FileContent(staging);
            }
            catch (FileNotFoundException) when (file.ItemId is not null)
            {
                // Retired and adopted meanwhile: the cache has this version
            }
        }
        var driveId = await file.Drive!.GetDriveIdAsync(ct);
        Touch(driveId);
        NoteRead(driveId, file);
        return content.Open(driveId, file.ItemId!, file.ContentTag, file.Size, current => ContentChanged(driveId, current));
    }

    /// <summary>
    /// The item at a path: from its folder's listing when that is current, else from a lookup (cached briefly). When Microsoft 365
    /// doesn't answer, the last known answer serves (cached files keep opening): at once for UnreachableFor after a failure, with one
    /// lookup retried in the background meanwhile. <paramref name="refreshParent"/> false: an internal lookup (a folder's ID) that
    /// neither reads the folder's stale listing again nor counts towards listing it.
    /// </summary>
    async Task<DriveItemInfo?> LookupAsync(string driveId, string drivePath, CancellationToken ct, bool refreshParent = true)
    {
        var name = NameOf(drivePath);
        Listing? stale = null, loading = null;
        if (drivePath.Length > 0)
        {
            var folder = ParentOf(drivePath);
            var currency = CurrencyOf(driveId, folder, out var parent);
            if (currency == Currency.FeedBehind)
            {
                // Kept by a feed not read lately (idle, asleep, just started): what changed meanwhile comes first, briefly. After a long
                // time away a catch-up still running doesn't count: the listing answers only as a last resort.
                await CatchUpAsync(driveId);
                currency = CurrencyOf(driveId, folder, out parent, caughtUp: CaughtUp(driveId));
            }
            if (currency == Currency.Current)
            {
                parent.LastUsed = Now;
                return parent.Items.GetValueOrDefault(name);
            }
            stale = parent is { IsLoaded: true } ? parent : null;
            loading = parent is { IsLoading: true } ? parent : null;
            if (currency == Currency.No && refreshParent && stale is not null && Now >= stale.RetryRefreshAt && stale.TryStartRefresh())
            {
                _ = RefreshAsync(driveId, folder, Key(driveId, folder), stale); // read again once, rather than a lookup per file meanwhile
            }
        }

        var key = Key(driveId, drivePath);
        var remembered = lookups.TryGetValue(key, out var known);
        if (remembered && Now - known.At < (known.Item is null ? options.NotFoundTtl : options.LookupTtl))
        {
            return known.Item;
        }
        if (loading is not null && loading.Items.TryGetValue(name, out var arrived))
        {
            return arrived; // its folder is being read right now
        }
        if (drivePath.Length > 0 && AboveIsMissing(driveId, drivePath))
        {
            lookups[key] = (null, Now);
            return null;
        }

        // The last known answer, for when Microsoft 365 doesn't answer
        var (hasFallback, fallback) = stale is not null ? (true, stale.Items.GetValueOrDefault(name)) : remembered ? (true, known.Item) : (false, null);
        if (hasFallback && unreachable.TryGetValue(driveId, out var down))
        {
            if (Now >= down.Until && down.TryStartProbe())
            {
                _ = ProbeAsync(driveId, drivePath, key, down);
            }
            return fallback;
        }
        if (drivePath.Length > 0 && refreshParent)
        {
            NoteLookup(driveId, ParentOf(drivePath));
        }
        var started = Now;
        var call = api.GetItemAsync(driveId, drivePath, ct);
        DriveItemInfo? item;
        try
        {
            item = hasFallback ? await call.WaitAsync(options.LookupDeadline, ct) : await call;
        }
        catch (TimeoutException) when (!call.IsCompleted)
        {
            // Slow to answer (a black-holed network, a long Retry-After): the last known answer serves, the lookup goes on
            Unreachable(driveId);
            _ = RememberLateAsync(driveId, key, call, started);
            return fallback;
        }
        catch (Exception e) when (!ct.IsCancellationRequested && IsTransient(e, readOnlyCounts: false))
        {
            if (Unanswered(e))
            {
                Unreachable(driveId);
            }
            if (!hasFallback)
            {
                throw;
            }
            return fallback; // offline or Microsoft 365 unreachable: cached files keep opening
        }
        Reachable(driveId);
        Remember(key, item, started);
        return item;
    }

    /// <summary>Caches a lookup's answer, unless something newer was recorded since it was asked (a local write).</summary>
    void Remember(string key, DriveItemInfo? item, DateTimeOffset askedAt) =>
        lookups.AddOrUpdate(key, (item, Now), (_, known) => known.At > askedAt ? known : (item, Now));

    async Task RememberLateAsync(string driveId, string key, Task<DriveItemInfo?> call, DateTimeOffset askedAt)
    {
        try
        {
            Remember(key, await call, askedAt);
            Reachable(driveId);
        }
        catch (Exception e) when (Unanswered(e))
        {
            Unreachable(driveId);
        }
        catch (Exception)
        {
            // the next lookup asks again
        }
    }

    /// <summary>A lookup in the background while lookups of a drive answer from what is cached: once it works, they ask again.</summary>
    async Task ProbeAsync(string driveId, string drivePath, string key, Reach down)
    {
        RequestPriority.MarkBackground();
        var started = Now;
        try
        {
            Remember(key, await CallAsync(ct => api.GetItemAsync(driveId, drivePath, ct)), started);
            Reachable(driveId);
        }
        catch (Exception e) when (Unanswered(e))
        {
            Unreachable(driveId);
        }
        catch (Exception)
        {
            Reachable(driveId); // an answer (no access, say): Microsoft 365 can be reached
        }
        finally
        {
            down.EndProbe();
        }
    }

    /// <summary>A drive whose reads failed on the network: until Until, lookups with a last known answer use it without asking.</summary>
    sealed class Reach
    {
        long until, probing;

        public DateTimeOffset Until
        {
            get => new(Volatile.Read(ref until), TimeSpan.Zero);
            set => Volatile.Write(ref until, value.UtcTicks);
        }

        public bool TryStartProbe() => Interlocked.CompareExchange(ref probing, 1, 0) == 0;

        public void EndProbe() => Volatile.Write(ref probing, 0);
    }

    readonly ConcurrentDictionary<string, Reach> unreachable = new(StringComparer.Ordinal);

    /// <summary>After a read failed on the network, lookups answer from what is cached at once for this long, then one tries again.</summary>
    static readonly TimeSpan UnreachableFor = TimeSpan.FromSeconds(30);

    void Unreachable(string driveId) => unreachable.GetOrAdd(driveId, _ => new Reach()).Until = Now + UnreachableFor;

    /// <summary>A failure without an answer from Microsoft 365 (no network, a timeout, no token): not a 404, a 403 or a 503.</summary>
    static bool Unanswered(Exception e) => e is not (RemoteException or FsException);

    void Reachable(string driveId)
    {
        if (!unreachable.IsEmpty)
        {
            unreachable.TryRemove(driveId, out _);
        }
    }

    /// <summary>A folder above the path is known not to be there (or is a file), so nothing is at the path either.</summary>
    bool AboveIsMissing(string driveId, string drivePath)
    {
        for (var path = ParentOf(drivePath); path.Length > 0; path = ParentOf(path))
        {
            if (lookups.TryGetValue(Key(driveId, path), out var known) && Now - known.At < (known.Item is null ? options.NotFoundTtl : options.LookupTtl))
            {
                return known.Item is not { IsFolder: true };
            }
            if (CurrencyOf(driveId, ParentOf(path), out var listing) == Currency.Current)
            {
                return listing.Items.GetValueOrDefault(NameOf(path)) is not { IsFolder: true };
            }
        }
        return false;
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
        Touch(driveId, engage: folderPath.Length > 0 || (listings.GetValueOrDefault(key) is { } earlier && Now - earlier.FirstShown(Now) >= ShallowGrace));
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
                // What changed since it was asked for goes in before anyone takes it as loaded
                listing.Finish(Replay(driveId, folderPath, listing) ? Now : DateTimeOffset.MinValue);
            }
            Reachable(driveId);
            unlisted.TryRemove(key, out _);
            PrefetchChildren(driveId, folderPath, listing);
            TrackAncestors(driveId, folderPath);
        }
        catch (Exception e)
        {
            if (Unanswered(e))
            {
                Unreachable(driveId);
            }
            unlisted[key] = Now;
            listings.TryRemove(new KeyValuePair<string, Listing>(key, listing));
            listing.Fail(e);
        }
    }

    /// <summary>Folders whose listing or lookup failed lately (no access to it, for one): not looked up again as a folder above another for a while.</summary>
    readonly ConcurrentDictionary<string, DateTimeOffset> unlisted = new(StringComparer.OrdinalIgnoreCase);

    static readonly TimeSpan UnlistedRetry = TimeSpan.FromMinutes(10);

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
            var fresh = new Listing(requestedAt, capacity: Math.Max(items.Count, 1)) { FolderId = folderId ?? stale.FolderId, ParentId = parentId ?? stale.ParentId, LastUsed = stale.LastUsed };
            fresh.AddPage(items);
            var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
            lock (feed.ApplyGate)
            {
                if (listings.GetValueOrDefault(key) != stale)
                {
                    return;
                }
                fresh.Finish(Replay(driveId, folderPath, fresh) ? Now : DateTimeOffset.MinValue);
                if (!listings.TryUpdate(key, fresh, stale))
                {
                    return;
                }
            }
            Reachable(driveId);
            ReportDifferences(driveId, folderPath, stale, fresh);
            TrackAncestors(driveId, folderPath);
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
            if (Unanswered(e))
            {
                Unreachable(driveId);
            }
            stale.RetryRefreshAt = Now + options.RetryInterval;
            Log($"refresh of {folderPath} failed, showing the cached listing: {e.Message}");
        }
        finally
        {
            stale.EndRefresh();
        }
    }

    /// <summary>
    /// The folder's ID and its parent's ID, from cached listings where they answer, else from Graph; nulls when that fails. Stale
    /// listings above it are not read again for this (one lookup instead).
    /// </summary>
    async Task<(string? Id, string? ParentId)> FolderOrNullAsync(string driveId, string folderPath)
    {
        try
        {
            if (folderPath.Length == 0)
            {
                return (await CallAsync(ct => FolderIdAsync(driveId, "", ct)), null);
            }
            var parentId = TryGetCurrentListing(driveId, ParentOf(folderPath), out var parent) ? parent.FolderId : null;
            var folder = await CallAsync(ct => LookupAsync(driveId, folderPath, ct, refreshParent: false));
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
        Reachable(driveId);
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

    /// <summary>
    /// A local write onto a listing. Replayed, it doesn't put back an older version than the listing has. An item under the same name
    /// replaces the old one in one step, so a lookup never finds the name empty in between.
    /// </summary>
    static void ApplyLocal(Listing listing, string drivePath, DriveItemInfo? item, bool replay)
    {
        var name = NameOf(drivePath);
        if (replay && item is not null && listing.Items.TryGetValue(name, out var known) && known.Id == item.Id && IsOlder(item.ETag, known.ETag))
        {
            return;
        }
        if (item is null || !item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            listing.Remove(name, out _);
        }
        if (item is not null)
        {
            listing.Set(item);
        }
    }

    /// <summary>
    /// Drops what is cached at and below a path, so it is read again. The folder's listing it is in stays: a folder is just taken out
    /// (the caller records what is there now), and for a file the listing is read again in the background to show the server's version.
    /// </summary>
    void Invalidate(string driveId, string drivePath)
    {
        var parent = listings.GetValueOrDefault(Key(driveId, ParentOf(drivePath)));
        var known = parent is { IsLoaded: true } ? parent.Items.GetValueOrDefault(NameOf(drivePath)) : null;
        DropBelow(driveId, drivePath);
        if (known is { IsFolder: false })
        {
            parent!.Expire();
        }
        else if (known is not null)
        {
            parent!.Remove(known.Name, out _);
        }
    }

    /// <summary>Removes cached listings, lookups and known folders at and below a path ("" for all of the drive).</summary>
    void DropBelow(string driveId, string drivePath)
    {
        var key = Key(driveId, drivePath);
        var below = drivePath.Length == 0 ? key : key + "/";
        bool Gone(string k) => k.StartsWith(below, StringComparison.OrdinalIgnoreCase) || k.Equals(key, StringComparison.OrdinalIgnoreCase);
        foreach (var (k, _) in listings)
        {
            if (Gone(k))
            {
                listings.TryRemove(k, out _);
            }
        }
        foreach (var (k, _) in lookups)
        {
            if (Gone(k))
            {
                lookups.TryRemove(k, out _);
            }
        }
        foreach (var (k, _) in knownFolders)
        {
            if (Gone(k))
            {
                knownFolders.TryRemove(k, out _);
            }
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
            Creator = pending.Creator,
            Drive = drive,
            DriveId = pending.DriveId,
            DrivePath = pending.DrivePath,
        };
        bindings.AddOrUpdate(entry, pending);
        return entry;
    }

    /// <summary><see cref="PendingEntry"/>, or null when the content was discarded (not uploaded) meanwhile: look the path up again.</summary>
    FsEntry? LivePendingEntry(string path, DriveMount drive, PendingFile pending)
    {
        var entry = PendingEntry(path, drive, pending);
        return pending.Discarded && pending.Result is null ? null : entry;
    }

    static string Join(string path, string name) => path == "\\" ? "\\" + name : path + "\\" + name;

    internal static string JoinDrivePath(string path, string name) => path.Length == 0 ? name : path + "/" + name;

    internal static string ParentOf(string drivePath) => drivePath.LastIndexOf('/') is var split and >= 0 ? drivePath[..split] : "";

    internal static string NameOf(string drivePath) => drivePath[(drivePath.LastIndexOf('/') + 1)..];
}
