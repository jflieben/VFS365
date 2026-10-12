using System.Collections.Concurrent;

namespace Vfs365.Core.Drive;

/// <summary>A cached folder listing as kept between runs. FetchedAt and ParentId are missing in files written by older versions.</summary>
public sealed record FolderSnapshot(string Path, string FolderId, IReadOnlyList<DriveItemInfo> Items, DateTimeOffset? FetchedAt = null, string? ParentId = null);

/// <summary>A drive's cached listings with the delta link that continues from the moment they reflect.</summary>
public sealed record DriveSnapshot(string DriveId, string DeltaLink, DateTimeOffset SavedAt, IReadOnlyList<FolderSnapshot> Folders);

/// <summary>
/// Change feed per drive in use: Graph delta started at "now" (token=latest, so nothing is enumerated up front) and applied to
/// the cached listings it covers. Those stay current without being listed again, and what changed is reported to the front end.
/// A drive's feed is read when the drive is used and PollInterval passed, in the background while it is active, or when a push
/// notification asks for it.
/// </summary>
public sealed partial class DriveEngine
{
    sealed class Feed
    {
        public string? DeltaLink { get; set; }
        public DateTimeOffset BaselineAt { get; set; } = DateTimeOffset.MaxValue;
        public DateTimeOffset LastPoll { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastUsed { get; set; }
        public bool Broken { get; set; }
        public bool Pushed { get; set; }
        public Task? Polling { get; set; }

        /// <summary>Really used: more than the library's top listed once. Only then the feed runs and the drive counts as active.</summary>
        public bool Engaged { get; set; }

        /// <summary>Reads in a row that brought no change: with push up, hot polling slows down.</summary>
        public int EmptyReads { get; set; }

        /// <summary>Held while changes are applied to listings, or replayed onto one that just loaded.</summary>
        public Lock ApplyGate { get; } = new();

        /// <summary>
        /// Changes applied lately, with when: a listing that was loading meanwhile gets those from after its request replayed.
        /// Changes up to DroppedUpTo were let go.
        /// </summary>
        public List<(DateTimeOffset At, DriveChange Change)> Recent { get; } = [];
        public DateTimeOffset DroppedUpTo { get; set; } = DateTimeOffset.MinValue;

        /// <summary>Reads that failed in a row; no read before RetryAt. Problem is what the log last said about it.</summary>
        public int Failures { get; set; }
        public DateTimeOffset RetryAt { get; set; }
        public string? Problem { get; set; }

        /// <summary>Folders of this drive that started loading lately, to notice a tree walk and find where it is.</summary>
        public Queue<(DateTimeOffset At, string Path)> Loads { get; } = new();

        /// <summary>Folders opened lately (loaded or cached), to see whether loads go down the tree.</summary>
        public Dictionary<string, DateTimeOffset> Opened { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>A tree walk is going on until then: subfolders below WalkRoot are loaded ahead.</summary>
        public DateTimeOffset WalkingUntil { get; set; }

        public string WalkRoot { get; set; } = "";

        /// <summary>Folders to load ahead in the order they were found: a depth-first walker takes the newest, a breadth-first one the oldest.</summary>
        public List<string> Candidates { get; } = [];

        /// <summary>The walker's last folder, and its recent steps: 1 into a child of the folder before, -1 up to a shallower one, 0 otherwise.</summary>
        public string? LastOpened { get; set; }
        public Queue<int> Steps { get; } = new();

        /// <summary>
        /// Depth-first walkers (Explorer, robocopy) go into children and back up; breadth-first ones (.NET's recursive enumeration) read a
        /// level at a time and never go back up.
        /// </summary>
        public bool DepthFirst => Steps.Count < 4 || Steps.Contains(-1) || Steps.Count(step => step == 1) >= 2;

        /// <summary>Folders loaded ahead (or loading) that the walk hasn't opened yet.</summary>
        public HashSet<string> Ahead { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int InFlight { get; set; }
        public int LoadedAhead { get; set; }
        public int UsedAhead { get; set; }

        /// <summary>Listings requested after the baseline get every later change from this feed.</summary>
        public bool Maintains(DateTimeOffset requestedAt) => DeltaLink is not null && !Broken && requestedAt >= BaselineAt;
    }

    readonly ConcurrentDictionary<string, Feed> feeds = new(StringComparer.Ordinal);

    static Feed NewFeed() => new();

    /// <summary>How long an opened folder counts as part of a walk going down the tree.</summary>
    static readonly TimeSpan OpenedWindow = TimeSpan.FromMinutes(2);

    bool Maintained(string driveId, Listing listing) =>
        listing.FolderId is not null && feeds.TryGetValue(driveId, out var feed) && feed.Maintains(listing.RequestedAt);

    /// <summary>
    /// The drive is used. Without <paramref name="engage"/> (a glance at the library's top) a drive not used for real yet stays
    /// without feed, polling and push channel.
    /// </summary>
    void Touch(string driveId, bool engage = true)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        if (!engage && !feed.Engaged)
        {
            return;
        }
        feed.Engaged = true;
        feed.LastUsed = Now;
        var interval = !feed.Pushed ? options.PollInterval : HotPolling ? HotInterval(feed) : options.PushedPollInterval;
        if (Now - feed.LastPoll >= interval)
        {
            _ = PollAsync(driveId);
        }
    }

    /// <summary>This engine changed the drive: hot polling goes back to its base interval.</summary>
    void LocalWrite(string driveId)
    {
        if (feeds.TryGetValue(driveId, out var feed))
        {
            feed.EmptyReads = 0;
        }
        Interlocked.Increment(ref metadataVersion);
        Touch(driveId);
    }

    const int MaxHotBackoff = 3;

    /// <summary>HotPollInterval, doubled for each empty read in a row while push is up (20, 40, 80, 160 s by default).</summary>
    TimeSpan HotInterval(Feed feed) => feed.Pushed ? options.HotPollInterval * (1 << Math.Min(feed.EmptyReads, MaxHotBackoff)) : options.HotPollInterval;

    /// <summary>A push channel for the drive came up or went down. While up, changes are read when notified instead of by polling.</summary>
    public void SetPush(string driveId, bool connected)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        var was = feed.Pushed;
        feed.Pushed = connected;
        if (connected && !was)
        {
            _ = PollAsync(driveId); // catch up on what happened while the channel was down
        }
    }

    /// <summary>
    /// Reads a drive's changes now, or joins the read already running. After failures, not before the back-off ends. The drive's feed
    /// runs from now on (the drive counts as active only when used).
    /// </summary>
    public Task PollAsync(string driveId)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        feed.Engaged = true;
        lock (feed)
        {
            if (feed.Polling is { IsCompleted: false } running)
            {
                return running;
            }
            if (Now < feed.RetryAt)
            {
                return Task.CompletedTask;
            }
            feed.LastPoll = Now;
            return feed.Polling = Task.Run(() => PollCoreAsync(driveId, feed));
        }
    }

    /// <summary>
    /// Reads the drives that are due: used within HotWindow every HotPollInterval (the one being looked at shows changes sooner than
    /// push delivers them), else within ActiveWindow every BackgroundPollInterval, or PushedPollInterval with push.
    /// </summary>
    public Task PollActiveAsync()
    {
        FinishWalks();
        FinishReadWalks();
        Maintain();
        return Task.WhenAll(feeds
            .Where(f => Now - f.Value.LastUsed < options.ActiveWindow && Now - f.Value.LastPoll >= Interval(f.Value))
            .Select(f => PollAsync(f.Key)));
    }

    TimeSpan Interval(Feed feed) =>
        HotPolling && Now - feed.LastUsed < options.HotWindow ? HotInterval(feed)
        : feed.Pushed ? options.PushedPollInterval
        : options.BackgroundPollInterval;

    /// <summary>Off while the tenant throttles (set by the host), and when HotPollInterval is zero.</summary>
    public bool HotPollingPaused { get; set; }

    bool HotPolling => options.HotPollInterval > TimeSpan.Zero && !HotPollingPaused;

    /// <summary>Drives used within ActiveWindow (for push subscriptions).</summary>
    public IReadOnlyList<string> ActiveDrives() => feeds.Where(f => Now - f.Value.LastUsed < options.ActiveWindow).Select(f => f.Key).ToList();

    /// <summary>
    /// A catch-up longer than this many pages, on a drive with fewer cached listings than pages read, starts over from now instead:
    /// reading those folders again costs less than the rest of the catch-up.
    /// </summary>
    const int CatchUpPages = 10;

    async Task PollCoreAsync(string driveId, Feed feed)
    {
        RequestPriority.MarkBackground();
        try
        {
            if (feed.DeltaLink is not { } link)
            {
                await BaselineAsync(driveId, feed);
                return;
            }
            var changes = new List<DriveChange>();
            for (var pages = 1; ; pages++)
            {
                var page = await api.GetDeltaAsync(link, CancellationToken.None);
                changes.AddRange(page.Changes);
                if (page.NextLink is { } next)
                {
                    if (pages >= CatchUpPages && pages > CachedListings(driveId))
                    {
                        Log($"change feed of {FeedName(driveId)} is more than {pages} pages behind; starting over, folders are read again");
                        DropDrive(driveId);
                        await BaselineAsync(driveId, feed);
                        return;
                    }
                    link = next;
                    continue;
                }
                link = page.DeltaLink ?? link;
                break;
            }
            Apply(driveId, feed, changes);
            feed.EmptyReads = changes.Count == 0 ? feed.EmptyReads + 1 : 0;
            feed.DeltaLink = link;
            Recovered(driveId, feed);
        }
        catch (RemoteException e) when (e.Error == RemoteError.Gone)
        {
            // Expired: what is cached may have missed changes. Start over from now; folders are read again when used.
            Log($"change feed of {FeedName(driveId)} expired; folders are read again");
            DropDrive(driveId);
            try
            {
                await BaselineAsync(driveId, feed);
            }
            catch (Exception again) when (again is not OperationCanceledException)
            {
                feed.DeltaLink = null;
                Failed(driveId, feed, again);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Failed(driveId, feed, e);
        }
    }

    int CachedListings(string driveId)
    {
        var prefix = Key(driveId, "");
        return listings.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>Listings asked for after the link came back are covered by it (not those asked for while it was on its way).</summary>
    async Task BaselineAsync(string driveId, Feed feed)
    {
        feed.DeltaLink = await api.GetLatestDeltaLinkAsync(driveId, CancellationToken.None);
        feed.BaselineAt = Now;
        Recovered(driveId, feed);
    }

    static readonly TimeSpan FirstFeedRetry = TimeSpan.FromMinutes(1), LastFeedRetry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A read failed. Listings fall back to ListingTtl (read again when opened) until one succeeds. Reads back off from 1 to 30
    /// minutes, and the log tells once per problem instead of on every attempt.
    /// </summary>
    void Failed(string driveId, Feed feed, Exception e)
    {
        feed.Broken = true;
        feed.Failures++;
        var wait = TimeSpan.FromTicks(Math.Min(LastFeedRetry.Ticks, FirstFeedRetry.Ticks << Math.Min(feed.Failures - 1, 10)));
        feed.RetryAt = Now + wait;
        var problem = e is RemoteException { Error: RemoteError.ReadOnly }
            ? "SharePoint has the library read-only for now (maintenance or a site move)"
            : e.Message;
        if (problem != feed.Problem)
        {
            Log($"change feed of {FeedName(driveId)}: {problem}. Folders are read again when opened; trying again in {wait.TotalMinutes:0} min, then less often");
            feed.Problem = problem;
        }
    }

    void Recovered(string driveId, Feed feed)
    {
        if (feed.Failures > 0)
        {
            Log($"change feed of {FeedName(driveId)} works again after {feed.Failures} failed read(s)");
        }
        feed.Broken = false;
        feed.Failures = 0;
        feed.RetryAt = default;
        feed.Problem = null;
    }

    /// <summary>The library's path on the volume, and its drive ID for support.</summary>
    string FeedName(string driveId) => ns.VolumePathOf(driveId) is { } path ? $"{path} (drive {driveId})" : $"drive {driveId}";

    /// <summary>
    /// The front end opened a folder of the drive. Many folder loads in a short time that also go down the tree mean a walk (copy,
    /// search, backup): while it lasts, subfolders inside the walked subtree are loaded ahead, PrefetchConcurrency at a time and never
    /// more than PrefetchAhead that the walk hasn't opened yet, in the walker's own order. The walk would list them anyway. Loading
    /// ahead stops WalkIdle after the walker does and pauses while the tenant throttles.
    /// </summary>
    void NoteWalker(string driveId, string folderPath, bool startedLoad)
    {
        if (options.WalkThreshold <= 0 || options.PrefetchAhead <= 0 || !feeds.TryGetValue(driveId, out var feed))
        {
            return;
        }
        List<string> frontier;
        lock (feed)
        {
            if (feed.Ahead.Remove(folderPath))
            {
                feed.UsedAhead++;
            }
            if (feed.LastOpened is { } previous && !previous.Equals(folderPath, StringComparison.OrdinalIgnoreCase))
            {
                feed.Steps.Enqueue(folderPath.Length > 0 && ParentOf(folderPath).Equals(previous, StringComparison.OrdinalIgnoreCase) ? 1
                    : Depth(folderPath) < Depth(previous) ? -1 : 0);
                if (feed.Steps.Count > 16)
                {
                    feed.Steps.Dequeue();
                }
            }
            feed.LastOpened = folderPath;
            if (Now < feed.WalkingUntil)
            {
                feed.WalkingUntil = Now + options.WalkIdle;
                if (!InWalk(feed, folderPath))
                {
                    feed.WalkRoot = CommonAncestor([feed.WalkRoot, folderPath]); // the walk moved on to a wider part of the tree
                }
                frontier = [];
            }
            else
            {
                feed.Opened[folderPath] = Now;
                if (feed.Opened.Count > 512)
                {
                    foreach (var old in feed.Opened.Where(o => Now - o.Value > OpenedWindow).Select(o => o.Key).ToList())
                    {
                        feed.Opened.Remove(old);
                    }
                }
                if (!startedLoad)
                {
                    return;
                }
                feed.Loads.Enqueue((Now, folderPath));
                while (feed.Loads.TryPeek(out var oldest) && Now - oldest.At > options.WalkWindow)
                {
                    feed.Loads.Dequeue();
                }
                if (feed.Loads.Count < options.WalkThreshold || !feed.Loads.Any(load => GoesDown(feed, load.Path)))
                {
                    return;
                }
                feed.WalkRoot = CommonAncestor(feed.Loads.Select(l => l.Path).ToList());
                // Everything the walk opened lately inside its root, in the order it did: their subfolders are the first candidates
                frontier = feed.Opened.Where(o => Now - o.Value < OpenedWindow && InWalk(feed, o.Key)).OrderBy(o => o.Value).Select(o => o.Key).ToList();
                feed.Loads.Clear();
                feed.Opened.Clear();
                feed.Candidates.Clear();
                feed.Ahead.Clear();
                feed.LoadedAhead = feed.UsedAhead = 0;
                feed.WalkingUntil = Now + options.WalkIdle;
            }
        }
        if (frontier.Count == 0)
        {
            Pump(driveId, feed); // a folder loaded ahead may have been opened: room for the next
            return;
        }
        Log($"tree walk on drive {driveId} below '{feed.WalkRoot}': loading up to {options.PrefetchAhead} folders ahead");
        foreach (var path in frontier)
        {
            if (listings.TryGetValue(Key(driveId, path), out var listing) && listing.IsLoaded)
            {
                PrefetchChildren(driveId, path, listing);
            }
        }
    }

    /// <summary>The folder's parent and grandparent were opened lately: the loads go down the tree, not just across one folder.</summary>
    bool GoesDown(Feed feed, string path)
    {
        if (!path.Contains('/'))
        {
            return false;
        }
        var parent = ParentOf(path);
        return Recent(parent) && Recent(ParentOf(parent));

        bool Recent(string folder) => feed.Opened.TryGetValue(folder, out var at) && Now - at < OpenedWindow;
    }

    /// <summary>The deepest folder that contains all the paths ("" for the drive root).</summary>
    static string CommonAncestor(IReadOnlyList<string> paths)
    {
        var common = paths[0].Split('/', StringSplitOptions.RemoveEmptyEntries);
        var length = common.Length;
        foreach (var path in paths.Skip(1))
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            length = Math.Min(length, segments.Length);
            for (var i = 0; i < length; i++)
            {
                if (!segments[i].Equals(common[i], StringComparison.OrdinalIgnoreCase))
                {
                    length = i;
                    break;
                }
            }
        }
        return string.Join('/', common.Take(length));
    }

    static int Depth(string path) => path.Length == 0 ? 0 : path.Count(c => c == '/') + 1;

    static bool InWalk(Feed feed, string path) =>
        feed.WalkRoot.Length == 0 || path.Equals(feed.WalkRoot, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(feed.WalkRoot + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>Set by the host while the tenant throttles: walks then go at the walker's own pace.</summary>
    public bool PrefetchPaused { get; set; }

    const int MaxCandidates = 10_000;

    /// <summary>A folder finished loading: during a walk inside its subtree, its subfolders are the next to load ahead.</summary>
    void PrefetchChildren(string driveId, string folderPath, Listing listing)
    {
        if (!feeds.TryGetValue(driveId, out var feed))
        {
            return;
        }
        lock (feed)
        {
            if (Now >= feed.WalkingUntil || !InWalk(feed, folderPath))
            {
                return;
            }
            // Walkers read subfolders by name: a depth-first one takes the newest candidate, so those go in reversed
            var children = listing.Items.Values.Where(i => i.IsFolder).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).Select(i => JoinDrivePath(folderPath, i.Name));
            feed.Candidates.AddRange(feed.DepthFirst ? children.Reverse() : children);
            if (feed.Candidates.Count > MaxCandidates)
            {
                feed.Candidates.RemoveRange(0, feed.Candidates.Count - MaxCandidates);
            }
        }
        Pump(driveId, feed);
    }

    /// <summary>Starts loads ahead while the walk goes on, a slot is free and fewer than PrefetchAhead loaded folders wait for the walker.</summary>
    void Pump(string driveId, Feed feed)
    {
        while (true)
        {
            string? next = null;
            Listing? listing = null;
            lock (feed)
            {
                if (PrefetchPaused || Now >= feed.WalkingUntil || feed.InFlight >= options.PrefetchConcurrency || feed.Ahead.Count >= options.PrefetchAhead)
                {
                    return;
                }
                var newest = feed.DepthFirst;
                while (feed.Candidates.Count > 0 && listing is null)
                {
                    var at = newest ? feed.Candidates.Count - 1 : 0;
                    if (newest && at > 0 && feed.LastOpened is { } last && ParentOf(feed.Candidates[at]).Equals(last, StringComparison.OrdinalIgnoreCase)
                        && !listings.ContainsKey(Key(driveId, feed.Candidates[at])))
                    {
                        at--; // the walker opens that first subfolder itself right now; load the ones after it
                    }
                    var candidate = feed.Candidates[at];
                    feed.Candidates.RemoveAt(at);
                    var fresh = new Listing(Now);
                    if (listings.TryAdd(Key(driveId, candidate), fresh))
                    {
                        (next, listing) = (candidate, fresh);
                    }
                }
                if (next is null || listing is null)
                {
                    return;
                }
                feed.InFlight++;
                feed.Ahead.Add(next);
                feed.LoadedAhead++;
            }
            _ = PrefetchAsync(driveId, next, listing, feed);
        }
    }

    async Task PrefetchAsync(string driveId, string folderPath, Listing listing, Feed feed)
    {
        RequestPriority.MarkBackground();
        await Task.Yield(); // never finish inside Pump's loop
        try
        {
            await LoadAsync(driveId, folderPath, Key(driveId, folderPath), listing);
        }
        finally
        {
            lock (feed)
            {
                feed.InFlight--;
                if (!listing.IsLoaded)
                {
                    feed.Ahead.Remove(folderPath);
                }
            }
            Pump(driveId, feed);
        }
    }

    /// <summary>Logs how loading ahead went for walks that ended, and forgets what was left to load.</summary>
    void FinishWalks()
    {
        foreach (var (driveId, feed) in feeds)
        {
            int loaded, used;
            string root;
            lock (feed)
            {
                if (Now < feed.WalkingUntil || (feed.LoadedAhead == 0 && feed.Candidates.Count == 0))
                {
                    continue;
                }
                (loaded, used, root) = (feed.LoadedAhead, feed.UsedAhead, feed.WalkRoot);
                feed.Candidates.Clear();
                feed.Ahead.Clear();
                feed.LoadedAhead = feed.UsedAhead = 0;
            }
            Log($"tree walk on drive {driveId} below '{root}' ended: {loaded} folder(s) loaded ahead, {used} of them opened");
        }
    }

    static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(10);
    const int MaxRecent = 20_000;

    /// <summary>Applies changes read from the feed to the cached listings and lookups of a drive, reports them and keeps them for replays.</summary>
    void Apply(string driveId, Feed feed, IReadOnlyList<DriveChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }
        Log($"change feed of drive {driveId}: {changes.Count} change(s)");
        lock (feed.ApplyGate)
        {
            var at = Now;
            feed.Recent.AddRange(changes.Select(change => (at, change)));
            var drop = feed.Recent.FindIndex(r => at - r.At <= RecentWindow);
            drop = Math.Max(drop < 0 ? feed.Recent.Count : drop, feed.Recent.Count - MaxRecent);
            if (drop > 0)
            {
                feed.DroppedUpTo = feed.Recent[drop - 1].At;
                feed.Recent.RemoveRange(0, drop);
            }
            ApplyCore(driveId, changes, null);
        }
    }

    /// <summary>
    /// Called under the feed's ApplyGate when a listing finished loading or was read again: changes applied since it was asked for
    /// may be missing from it, so they are applied to it again. When some of those were let go it is marked stale instead.
    /// </summary>
    void Replay(string driveId, string folderPath, Listing listing)
    {
        if (!feeds.TryGetValue(driveId, out var feed))
        {
            return;
        }
        if (listing.RequestedAt <= feed.DroppedUpTo)
        {
            listing.Expire();
            return;
        }
        var missed = feed.Recent.Where(r => r.At >= listing.RequestedAt).Select(r => r.Change).ToList();
        if (missed.Count > 0)
        {
            ApplyCore(driveId, missed, folderPath);
        }
    }

    /// <summary>
    /// Applies changes to the drive's loaded listings (or only to <paramref name="onlyPath"/>'s) by item ID. A folder whose listing is
    /// cached and that moved, was renamed or deleted loses what is cached at and below its old path, whether or not its parent is
    /// cached; so do two listings that claim the same folder ID (one of them is stale).
    /// </summary>
    void ApplyCore(string driveId, IReadOnlyList<DriveChange> changes, string? onlyPath)
    {
        var prefix = Key(driveId, "");
        var all = listings.Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal) && l.Value.IsLoaded)
            .ToDictionary(l => l.Key[prefix.Length..], l => l.Value, StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = new List<string>();
        foreach (var (path, listing) in all)
        {
            if (listing.FolderId is { } folderId && !folders.TryAdd(folderId, path))
            {
                shared.Add(folders[folderId]);
                shared.Add(path);
            }
        }
        var cached = onlyPath is null ? all
            : all.TryGetValue(onlyPath, out var only) ? new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase) { [onlyPath] = only }
            : new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var path in shared.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            Log($"two cached listings of drive {driveId} claim one folder ID; '{path}' is read again");
            DropSubtree(path);
        }

        foreach (var change in changes)
        {
            if (change.Deleted && onlyPath is null)
            {
                content.ForgetItem(driveId, change.Id);
            }
            var item = change.Deleted ? null : change.Item;
            var newFolder = change.ParentId is { } parentId && folders.TryGetValue(parentId, out var folder) ? folder : null;

            if (folders.TryGetValue(change.Id, out var folderPath) && all.TryGetValue(folderPath, out var folderListing)
                && FolderMoved(folderPath, folderListing, item, change.ParentId))
            {
                var parentCached = all.ContainsKey(ParentOf(folderPath));
                DropSubtree(folderPath);
                if (!parentCached)
                {
                    Report(driveId, folderPath, ChangeKind.Removed, true);
                }
            }

            // Where the item is cached now: usually in the folder it is reported in
            string? wasFolder = null, wasName = null;
            Listing? wasListing = null;
            if (newFolder is not null && cached.TryGetValue(newFolder, out var here) && here.ById.TryGetValue(change.Id, out var hereName))
            {
                (wasFolder, wasListing, wasName) = (newFolder, here, hereName);
            }
            else
            {
                foreach (var (path, listing) in cached)
                {
                    if (listing.ById.TryGetValue(change.Id, out var name))
                    {
                        (wasFolder, wasListing, wasName) = (path, listing, name);
                        break;
                    }
                }
            }
            var moved = wasListing is not null && (item is null || newFolder is null || !wasFolder!.Equals(newFolder, StringComparison.OrdinalIgnoreCase) || wasName != item.Name);
            if (moved && wasListing!.Items.TryGetValue(wasName!, out var old) && old.Id == change.Id)
            {
                var oldPath = JoinDrivePath(wasFolder!, wasName!);
                wasListing.Remove(wasName!, out _);
                lookups.TryRemove(Key(driveId, oldPath), out _);
                if (old.IsFolder)
                {
                    DropSubtree(oldPath);
                }
                Report(driveId, oldPath, ChangeKind.Removed, old.IsFolder);
                changed = true;
            }
            if (item is null || newFolder is null || !cached.TryGetValue(newFolder, out var target))
            {
                continue;
            }
            var itemPath = JoinDrivePath(newFolder, item.Name);
            var existing = target.Items.GetValueOrDefault(item.Name);
            if (existing is not null && existing.Id == item.Id && existing.ETag == item.ETag && existing.Size == item.Size)
            {
                continue; // already known, for example an echo of this engine's own upload
            }
            target.Set(item);
            lookups.TryRemove(Key(driveId, itemPath), out _);
            Report(driveId, itemPath, existing is null ? ChangeKind.Added : ChangeKind.Modified, item.IsFolder);
            changed = true;
        }
        if (changed)
        {
            Interlocked.Increment(ref metadataVersion);
        }

        // Is the folder listed at folderPath somewhere else now (or gone)? Only the name and, where known, the parent tell: a change to
        // anything inside a folder also reports the folder itself.
        bool FolderMoved(string folderPath, Listing listing, DriveItemInfo? item, string? parentId)
        {
            if (item is null)
            {
                return true;
            }
            if (folderPath.Length == 0)
            {
                return false;
            }
            if (!item.Name.Equals(NameOf(folderPath), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            var expected = all.TryGetValue(ParentOf(folderPath), out var parent) && parent.FolderId is { } id ? id : listing.ParentId;
            if (expected is null || parentId is null)
            {
                listing.ParentId ??= parentId;
                return false;
            }
            return expected != parentId;
        }

        // Drops what is cached at and below a path, here and in the maps this pass uses
        void DropSubtree(string path)
        {
            DropBelow(driveId, path);
            foreach (var gone in all.Keys.Where(k => k.Equals(path, StringComparison.OrdinalIgnoreCase) || path.Length == 0 || k.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                if (all.Remove(gone, out var listing) && listing.FolderId is { } id && folders.GetValueOrDefault(id) is { } mapped && mapped.Equals(gone, StringComparison.OrdinalIgnoreCase))
                {
                    folders.Remove(id);
                }
                cached.Remove(gone);
            }
            changed = true;
        }
    }

    /// <summary>
    /// A download served other content than the listing says: the file changed since it was listed, or SharePoint serves an Office
    /// file with properties written into it. The listing takes the item as served, so opening it again reads it whole.
    /// </summary>
    void ContentChanged(string driveId, DriveItemInfo current)
    {
        servedLengths[Key(driveId, current.Id)] = (current.CTag ?? current.ETag, current.Size);
        var prefix = Key(driveId, "");
        string? path = null;
        var listed = 0L;
        foreach (var (key, listing) in listings)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && listing.IsLoaded && listing.ById.TryGetValue(current.Id, out var name)
                && listing.Items.TryGetValue(name, out var known) && known.Id == current.Id)
            {
                (path, listed) = (JoinDrivePath(key[prefix.Length..], known.Name), known.Size);
                listing.Set(current with { Name = known.Name });
            }
        }
        foreach (var (key, lookup) in lookups)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && lookup.Item is { } known && known.Id == current.Id)
            {
                (path, listed) = (key[prefix.Length..], known.Size);
                lookups[key] = (current with { Name = known.Name }, lookup.At);
            }
        }
        if (path is null)
        {
            return;
        }
        Interlocked.Increment(ref metadataVersion);
        var shown = ns.VolumePathOf(driveId) is { } top ? Join(top, path.Replace('/', '\\')) : path;
        Log(listed == current.Size
            ? $"{shown}: changed in Microsoft 365 since it was listed (another version is served); a read failed, opening it again works"
            : $"{shown}: {listed} bytes listed, {current.Size} served (changed in Microsoft 365, or SharePoint wrote properties into it); " +
              "a read failed, opening it again works");
        Report(driveId, path, ChangeKind.Modified, false);
    }

    DateTimeOffset nextMaintenance;
    static readonly TimeSpan MaintenanceInterval = TimeSpan.FromSeconds(30);

    /// <summary>A listing used this recently is never evicted.</summary>
    static readonly TimeSpan ListingKeep = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Every 30 s (from <see cref="PollActiveAsync"/>): lookups past their TTL go, and listings beyond MaxCachedItems, least recently
    /// used first.
    /// </summary>
    void Maintain()
    {
        if (Now < nextMaintenance)
        {
            return;
        }
        nextMaintenance = Now + MaintenanceInterval;
        var lookupAge = options.LookupTtl > options.NotFoundTtl ? options.LookupTtl : options.NotFoundTtl;
        foreach (var lookup in lookups)
        {
            if (Now - lookup.Value.At >= lookupAge)
            {
                lookups.TryRemove(lookup);
            }
        }
        foreach (var burst in lookupBursts)
        {
            if (Now - burst.Value.Since > options.WalkWindow)
            {
                lookupBursts.TryRemove(burst);
            }
        }

        var size = listings.Values.Sum(l => (long)l.Items.Count + 1);
        if (size <= options.MaxCachedItems)
        {
            return;
        }
        var target = options.MaxCachedItems / 10 * 9;
        var evicted = 0;
        foreach (var old in listings.Where(l => l.Value.IsLoaded && Now - l.Value.LastUsed > ListingKeep).OrderBy(l => l.Value.LastUsed).ToList())
        {
            if (size <= target)
            {
                break;
            }
            if (listings.TryRemove(old))
            {
                size -= old.Value.Items.Count + 1;
                evicted++;
            }
        }
        if (evicted > 0)
        {
            Interlocked.Increment(ref metadataVersion);
            Log($"{evicted} folder listing(s) left memory (least recently used); they are read again when opened");
        }
    }

    /// <summary>Lengths downloads served that differ from the listed size, per item, for the content version they were served for.</summary>
    readonly ConcurrentDictionary<string, (string? Tag, long Length)> servedLengths = new(StringComparer.Ordinal);

    /// <summary>A file's size as its downloads serve it: listings and lookups bring SharePoint's own figure back.</summary>
    long ServedSize(string driveId, DriveItemInfo item) =>
        servedLengths.TryGetValue(Key(driveId, item.Id), out var served) && served.Tag == (item.CTag ?? item.ETag) ? served.Length : item.Size;

    /// <summary>Reports the difference between a listing and its refreshed version.</summary>
    void ReportDifferences(string driveId, string folderPath, Listing before, Listing after)
    {
        foreach (var old in before.Items.Values.Where(old => !after.Items.ContainsKey(old.Name)))
        {
            Report(driveId, JoinDrivePath(folderPath, old.Name), ChangeKind.Removed, old.IsFolder);
        }
        foreach (var now in after.Items.Values)
        {
            if (!before.Items.TryGetValue(now.Name, out var old))
            {
                Report(driveId, JoinDrivePath(folderPath, now.Name), ChangeKind.Added, now.IsFolder);
            }
            else if (old.Id != now.Id || old.ETag != now.ETag || old.Size != now.Size)
            {
                Report(driveId, JoinDrivePath(folderPath, now.Name), ChangeKind.Modified, now.IsFolder);
            }
        }
    }

    /// <summary>Forgets everything cached for a drive and tells the front end its top changed.</summary>
    void DropDrive(string driveId)
    {
        var prefix = Key(driveId, "");
        foreach (var key in listings.Keys.Concat(lookups.Keys).Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
        {
            listings.TryRemove(key, out _);
            lookups.TryRemove(key, out _);
        }
        if (feeds.TryGetValue(driveId, out var feed))
        {
            lock (feed.ApplyGate)
            {
                feed.Recent.Clear();
            }
        }
        Interlocked.Increment(ref metadataVersion);
        if (ns.VolumePathOf(driveId) is { } top)
        {
            options.Changed?.Invoke(new EngineChange(top, ChangeKind.Modified, true));
        }
    }

    void Report(string driveId, string drivePath, ChangeKind kind, bool isDirectory)
    {
        if (options.Changed is { } changed && ns.VolumePathOf(driveId) is { } top)
        {
            changed(new EngineChange(Join(top, drivePath.Replace('/', '\\')), kind, isDirectory));
        }
    }

    /// <summary>Replaces the libraries (discovery finished or changed) and reports the virtual folders whose content changed.</summary>
    public void SetLibraries(IReadOnlyList<Discovery.LibraryEntry> libraries)
    {
        foreach (var path in ns.SetLibraries(libraries))
        {
            options.Changed?.Invoke(new EngineChange(path, ChangeKind.Modified, true));
        }
    }

    /// <summary>Listings the change feeds keep current, with the links to continue from; per drive the most recently used first.</summary>
    public IReadOnlyList<DriveSnapshot> ExportSnapshots(int maxFoldersPerDrive)
    {
        var snapshots = new List<DriveSnapshot>();
        foreach (var (driveId, feed) in feeds)
        {
            if (feed.DeltaLink is not { } link || feed.Broken)
            {
                continue;
            }
            var prefix = Key(driveId, "");
            var folders = listings.Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal) && l.Value.IsLoaded && l.Value.FolderId is not null && feed.Maintains(l.Value.RequestedAt))
                .OrderByDescending(l => l.Value.LastUsed)
                .Take(maxFoldersPerDrive)
                .Select(l => new FolderSnapshot(l.Key[prefix.Length..], l.Value.FolderId!, l.Value.Items.Values.ToList(), l.Value.FetchedAt, l.Value.ParentId))
                .ToList();
            if (folders.Count > 0)
            {
                snapshots.Add(new DriveSnapshot(driveId, link, Now, folders));
            }
        }
        return snapshots;
    }

    /// <summary>
    /// Restores listings from an earlier run. They answer at once; the first read of each drive's feed (on first use) applies
    /// what changed since, and an expired link drops them. They are read again only after MaintainedListingMaxAge from when they
    /// were fetched.
    /// </summary>
    public void ImportSnapshots(IEnumerable<DriveSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            feeds[snapshot.DriveId] = new Feed { DeltaLink = snapshot.DeltaLink, BaselineAt = DateTimeOffset.MinValue, LastUsed = DateTimeOffset.MinValue, Engaged = true };
            foreach (var folder in snapshot.Folders)
            {
                var listing = Listing.Loaded(folder.Items, DateTimeOffset.MinValue, folder.FetchedAt ?? snapshot.SavedAt, folder.FolderId);
                listing.ParentId = folder.ParentId;
                listings.TryAdd(Key(snapshot.DriveId, folder.Path), listing);
            }
        }
    }
}
