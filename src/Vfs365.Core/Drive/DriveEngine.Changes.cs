using System.Collections.Concurrent;

namespace Vfs365.Core.Drive;

/// <summary>A cached folder listing as kept between runs.</summary>
public sealed record FolderSnapshot(string Path, string FolderId, IReadOnlyList<DriveItemInfo> Items);

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

    void Touch(string driveId)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        feed.LastUsed = Now;
        var interval = !feed.Pushed ? options.PollInterval : HotPolling ? options.HotPollInterval : options.PushedPollInterval;
        if (Now - feed.LastPoll >= interval)
        {
            _ = PollAsync(driveId);
        }
    }

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

    /// <summary>Reads a drive's changes now, or joins the read already running. After failures, not before the back-off ends.</summary>
    public Task PollAsync(string driveId)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
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
            var requestedAt = Now;
            return feed.Polling = Task.Run(() => PollCoreAsync(driveId, feed, requestedAt));
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
        return Task.WhenAll(feeds
            .Where(f => Now - f.Value.LastUsed < options.ActiveWindow && Now - f.Value.LastPoll >= Interval(f.Value))
            .Select(f => PollAsync(f.Key)));
    }

    TimeSpan Interval(Feed feed) =>
        HotPolling && Now - feed.LastUsed < options.HotWindow ? options.HotPollInterval
        : feed.Pushed ? options.PushedPollInterval
        : options.BackgroundPollInterval;

    /// <summary>Off while the tenant throttles (set by the host), and when HotPollInterval is zero.</summary>
    public bool HotPollingPaused { get; set; }

    bool HotPolling => options.HotPollInterval > TimeSpan.Zero && !HotPollingPaused;

    /// <summary>Drives used within ActiveWindow (for push subscriptions).</summary>
    public IReadOnlyList<string> ActiveDrives() => feeds.Where(f => Now - f.Value.LastUsed < options.ActiveWindow).Select(f => f.Key).ToList();

    /// <summary><paramref name="started"/> is when the read was asked for: listings asked for from then on count as covered by a new baseline.</summary>
    async Task PollCoreAsync(string driveId, Feed feed, DateTimeOffset started)
    {
        RequestPriority.MarkBackground();
        try
        {
            if (feed.DeltaLink is not { } link)
            {
                await BaselineAsync(driveId, feed, started);
                return;
            }
            var changes = new List<DriveChange>();
            while (true)
            {
                var page = await api.GetDeltaAsync(link, CancellationToken.None);
                changes.AddRange(page.Changes);
                if (page.NextLink is { } next)
                {
                    link = next;
                    continue;
                }
                link = page.DeltaLink ?? link;
                break;
            }
            Apply(driveId, changes);
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
                await BaselineAsync(driveId, feed, started);
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

    async Task BaselineAsync(string driveId, Feed feed, DateTimeOffset started)
    {
        feed.DeltaLink = await api.GetLatestDeltaLinkAsync(driveId, CancellationToken.None);
        feed.BaselineAt = started;
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

    /// <summary>Applies changes to the cached listings and lookups of a drive and reports them.</summary>
    void Apply(string driveId, IReadOnlyList<DriveChange> changes)
    {
        if (changes.Count == 0)
        {
            return;
        }
        Log($"change feed of drive {driveId}: {changes.Count} change(s)");
        var prefix = Key(driveId, "");
        var cached = listings.Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal) && l.Value.IsLoaded)
            .ToDictionary(l => l.Key[prefix.Length..], l => l.Value, StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var located = new Dictionary<string, (string Folder, string Name)>(StringComparer.Ordinal);
        foreach (var (folderPath, listing) in cached)
        {
            if (listing.FolderId is { } folderId)
            {
                folders[folderId] = folderPath;
            }
            foreach (var item in listing.Items.Values)
            {
                located[item.Id] = (folderPath, item.Name);
            }
        }

        foreach (var change in changes)
        {
            if (change.Deleted)
            {
                content.ForgetItem(driveId, change.Id);
            }
            var known = located.TryGetValue(change.Id, out var was);
            var newFolder = change.ParentId is { } parentId && folders.TryGetValue(parentId, out var folder) ? folder : null;
            var item = change.Deleted ? null : change.Item;
            var moved = known && (item is null || newFolder is null || !was.Folder.Equals(newFolder, StringComparison.OrdinalIgnoreCase) || was.Name != item.Name);
            if (moved && cached.TryGetValue(was.Folder, out var oldListing) && oldListing.Items.TryGetValue(was.Name, out var old) && old.Id == change.Id)
            {
                var oldPath = JoinDrivePath(was.Folder, was.Name);
                oldListing.Items.TryRemove(was.Name, out _);
                lookups.TryRemove(Key(driveId, oldPath), out _);
                if (old.IsFolder)
                {
                    DropBelow(driveId, oldPath);
                }
                located.Remove(change.Id);
                Report(driveId, oldPath, ChangeKind.Removed, old.IsFolder);
            }
            if (item is null || newFolder is null || !cached.TryGetValue(newFolder, out var listing))
            {
                continue;
            }
            var path = JoinDrivePath(newFolder, item.Name);
            var existing = listing.Items.GetValueOrDefault(item.Name);
            if (existing is not null && existing.Id == item.Id && existing.ETag == item.ETag && existing.Size == item.Size)
            {
                continue; // already known, for example an echo of this engine's own upload
            }
            listing.Items[item.Name] = item;
            located[item.Id] = (newFolder, item.Name);
            lookups.TryRemove(Key(driveId, path), out _);
            Report(driveId, path, existing is null ? ChangeKind.Added : ChangeKind.Modified, item.IsFolder);
        }
    }

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
                .Select(l => new FolderSnapshot(l.Key[prefix.Length..], l.Value.FolderId!, l.Value.Items.Values.ToList()))
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
    /// what changed since, and an expired link drops them.
    /// </summary>
    public void ImportSnapshots(IEnumerable<DriveSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            feeds[snapshot.DriveId] = new Feed { DeltaLink = snapshot.DeltaLink, BaselineAt = DateTimeOffset.MinValue, LastUsed = DateTimeOffset.MinValue };
            foreach (var folder in snapshot.Folders)
            {
                listings.TryAdd(Key(snapshot.DriveId, folder.Path), Listing.Loaded(folder.Items, DateTimeOffset.MinValue, snapshot.SavedAt, folder.FolderId));
            }
        }
    }
}
