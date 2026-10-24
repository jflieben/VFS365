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

        /// <summary>When the last read that worked started (or the baseline was taken): listings reflect the drive as of then.</summary>
        public DateTimeOffset LastRead { get; set; } = DateTimeOffset.MinValue;
        public DateTimeOffset LastUsed { get; set; }
        public bool Broken { get; set; }
        public bool Pushed { get; set; }

        /// <summary>When the push channel last came up; CatchUpAt is when the read for what happened while it was down is due.</summary>
        public DateTimeOffset PushedAt { get; set; }
        public DateTimeOffset? CatchUpAt { get; set; }

        /// <summary>A push notification came right after a read: the next read is due once PushGap passed since that one.</summary>
        public bool ReadSoon { get; set; }
        public Task? Polling { get; set; }

        /// <summary>When the running read started (Environment.TickCount64): lookups wait for it a few seconds from then at most.</summary>
        public long PollStartedTick { get; set; }

        /// <summary>Until then, opens and listings are taken as Explorer reading again what the engine just reported: no use of the drive.</summary>
        public DateTimeOffset EchoUntil { get; set; }

        /// <summary>Really used: more than the library's top listed once. Only then the feed runs and the drive counts as active.</summary>
        public bool Engaged { get; set; }

        /// <summary>
        /// Doublings of the hot interval: with push up every read since this engine last wrote to the drive (push brings changes), without
        /// push every read in a row that brought no change.
        /// </summary>
        public int Backoff { get; set; }

        /// <summary>Held while changes are applied to listings, or replayed onto one that just loaded.</summary>
        public Lock ApplyGate { get; } = new();

        /// <summary>
        /// Changes applied lately (from the feed and local writes), with when: a listing that was loading or being read again meanwhile
        /// gets those from after its request replayed. Changes up to DroppedUpTo were let go.
        /// </summary>
        public List<Recorded> Recent { get; } = [];
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

    /// <summary>A change applied lately: from the feed (Remote), or a local write at Path (Item null: nothing there now).</summary>
    readonly record struct Recorded(DateTimeOffset At, DriveChange? Remote, string? Path, DriveItemInfo? Item);

    readonly ConcurrentDictionary<string, Feed> feeds = new(StringComparer.Ordinal);

    static Feed NewFeed() => new();

    /// <summary>How long an opened folder counts as part of a walk going down the tree.</summary>
    static readonly TimeSpan OpenedWindow = TimeSpan.FromMinutes(2);

    /// <summary>How long after reporting changes the opens that follow count as Explorer reading them again.</summary>
    static readonly TimeSpan NotifyEcho = TimeSpan.FromSeconds(3);

    /// <summary>The read after a push channel came up waits up to this long at random, and is left out when a read started less than this before.</summary>
    static readonly TimeSpan PushCatchUp = TimeSpan.FromSeconds(30);

    /// <summary>Feeds backing off after failures are read again within this long at random after the network returns.</summary>
    static readonly TimeSpan NetworkJitter = TimeSpan.FromSeconds(30);

    /// <summary>A lookup in a listing whose feed wasn't read lately waits this long at most for the read it started.</summary>
    static readonly TimeSpan FeedCatchUpWait = TimeSpan.FromSeconds(3);

    /// <summary>
    /// The drive's change feed keeps the listing current: it has its folder ID, was asked for after the feed's baseline, and every
    /// folder above it is cached and lists it. A rename or move of a cached folder drops what is cached below it; one of a folder
    /// that isn't cached would go unnoticed, so below such a folder listings are only trusted for ListingTtl.
    /// </summary>
    bool Maintained(string driveId, string folderPath, Listing listing)
    {
        if (!feeds.TryGetValue(driveId, out var feed))
        {
            return false;
        }
        while (true)
        {
            if (listing.FolderId is not { } folderId || !feed.Maintains(listing.RequestedAt))
            {
                return false;
            }
            if (!folderPath.Contains('/'))
            {
                return true; // its parent is the drive's root, which is never renamed or moved
            }
            var parentPath = ParentOf(folderPath);
            if (!listings.TryGetValue(Key(driveId, parentPath), out var parent) || !parent.IsLoaded
                || !parent.ById.TryGetValue(folderId, out var name) || !name.Equals(NameOf(folderPath), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            (folderPath, listing) = (parentPath, parent);
        }
    }

    /// <summary>
    /// The feed was read lately: within two background intervals, or, with push up since before that read, within the pushed safety
    /// interval (a change would have been pushed).
    /// </summary>
    bool FeedCurrent(string driveId) =>
        feeds.TryGetValue(driveId, out var feed)
        && Now - feed.LastRead < (feed.Pushed && feed.LastRead >= feed.PushedAt ? options.PushedPollInterval : options.BackgroundPollInterval * 2);

    /// <summary>Reads the drive's changes or joins the read running, waiting FeedCatchUpWait from when that read started at most.</summary>
    async Task CatchUpAsync(string driveId)
    {
        var read = PollAsync(driveId);
        if (read.IsCompleted || !feeds.TryGetValue(driveId, out var feed))
        {
            return;
        }
        var left = feed.PollStartedTick + (long)FeedCatchUpWait.TotalMilliseconds - Environment.TickCount64;
        if (left > 0)
        {
            await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(left)));
        }
    }

    /// <summary>
    /// The drive is used. Without <paramref name="engage"/> (a glance at the library's top) a drive not used for real yet stays
    /// without feed, polling and push channel. Explorer reading again what the engine just reported doesn't count.
    /// </summary>
    void Touch(string driveId, bool engage = true)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        if ((!engage && !feed.Engaged) || (feed.Engaged && Now < feed.EchoUntil))
        {
            return;
        }
        feed.Engaged = true;
        feed.LastUsed = Now;
        if (Now - feed.LastPoll >= UseInterval(feed))
        {
            _ = PollAsync(driveId);
        }
    }

    TimeSpan UseInterval(Feed feed) => HotPolling ? HotInterval(feed) : feed.Pushed ? options.PushedPollInterval : options.PollInterval;

    /// <summary>This engine changed the drive: hot polling goes back to its base interval.</summary>
    void LocalWrite(string driveId)
    {
        if (feeds.TryGetValue(driveId, out var feed))
        {
            feed.Backoff = 0;
        }
        Interlocked.Increment(ref metadataVersion);
        Touch(driveId);
    }

    const int MaxPushedBackoff = 3, MaxPolledBackoff = 2;

    /// <summary>HotPollInterval, doubled per Backoff: up to 160 s by default with push, 80 s without.</summary>
    TimeSpan HotInterval(Feed feed) => options.HotPollInterval * (1 << Math.Min(feed.Backoff, feed.Pushed ? MaxPushedBackoff : MaxPolledBackoff));

    /// <summary>A push read this soon after the last read waits until this long after it: a push read and a hot check count as one.</summary>
    TimeSpan PushGap => options.HotPollInterval > TimeSpan.Zero ? options.HotPollInterval : options.PollInterval;

    /// <summary>
    /// A push channel for the drive came up or went down. While up, changes are read when notified instead of by polling. What happened
    /// while it was down is read 0 to 30 s after it came up (a push front end restart reconnects every client at once), unless a read
    /// started since or shortly before.
    /// </summary>
    public void SetPush(string driveId, bool connected)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        lock (feed)
        {
            var was = feed.Pushed;
            feed.Pushed = connected;
            if (connected && !was)
            {
                feed.PushedAt = Now;
                feed.CatchUpAt = Now + PushCatchUp * options.Random();
            }
            else if (!connected)
            {
                feed.CatchUpAt = null;
            }
        }
    }

    /// <summary>
    /// A push notification for the drive (already debounced): its changes are read now, or PushGap after the last read when that
    /// started less than PushGap ago.
    /// </summary>
    public Task NotifiedAsync(string driveId)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        lock (feed)
        {
            if (Now - feed.LastPoll < PushGap)
            {
                feed.ReadSoon = true;
                return Task.CompletedTask;
            }
        }
        return PollAsync(driveId);
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
            var started = Now;
            (feed.LastPoll, feed.ReadSoon, feed.PollStartedTick) = (started, false, Environment.TickCount64);
            return feed.Polling = Task.Run(() => PollCoreAsync(driveId, feed, started));
        }
    }

    /// <summary>
    /// Reads the drives that are due: used within HotWindow every hot interval (the one being looked at shows changes sooner than
    /// push delivers them), else within ActiveWindow every BackgroundPollInterval, or PushedPollInterval with push; also catch-up
    /// reads after a push channel came up, and push reads that waited for PushGap.
    /// </summary>
    public Task PollActiveAsync()
    {
        FinishWalks();
        FinishReadWalks();
        Maintain();
        return Task.WhenAll(feeds.Where(f => Due(f.Value)).Select(f => PollAsync(f.Key)));
    }

    bool Due(Feed feed)
    {
        lock (feed)
        {
            var since = Now - feed.LastPoll;
            if (feed.CatchUpAt is { } catchUp && Now >= catchUp)
            {
                // Unless a read started since the channel came up, or shortly before (opening a library again reads it already)
                feed.CatchUpAt = null;
                if (feed.PushedAt - feed.LastPoll >= PushCatchUp)
                {
                    return true;
                }
            }
            if (feed.ReadSoon && since >= PushGap)
            {
                return true;
            }
            return Now - feed.LastUsed < options.ActiveWindow && since >= Interval(feed);
        }
    }

    TimeSpan Interval(Feed feed) =>
        HotPolling && Now - feed.LastUsed < options.HotWindow ? HotInterval(feed)
        : feed.Pushed ? options.PushedPollInterval
        : options.BackgroundPollInterval;

    /// <summary>Off while the tenant throttles (set by the host), and when HotPollInterval is zero.</summary>
    public bool HotPollingPaused { get; set; }

    bool HotPolling => options.HotPollInterval > TimeSpan.Zero && !HotPollingPaused;

    /// <summary>Drives used within ActiveWindow.</summary>
    public IReadOnlyList<string> ActiveDrives() => feeds.Where(f => Now - f.Value.LastUsed < options.ActiveWindow).Select(f => f.Key).ToList();

    /// <summary>Drives used within ActiveWindow whose change feed works: they get push channels (while a feed fails, a push reads nothing).</summary>
    public IReadOnlyList<string> PushDrives() => feeds.Where(f => Now - f.Value.LastUsed < options.ActiveWindow && !f.Value.Broken).Select(f => f.Key).ToList();

    /// <summary>The network is back: feeds backing off after failures are read again soon, each 0 to 30 s from now at random.</summary>
    public void NetworkRestored()
    {
        foreach (var feed in feeds.Values)
        {
            lock (feed)
            {
                if (feed.RetryAt > Now)
                {
                    feed.RetryAt = Now + NetworkJitter * options.Random();
                }
            }
        }
    }

    /// <summary>
    /// A catch-up longer than this many pages, on a drive with fewer cached listings than pages read, starts over from now instead:
    /// reading those folders again costs less than the rest of the catch-up.
    /// </summary>
    const int CatchUpPages = 10;

    /// <summary>A read that fails or gets no answer within CallTimeout (whatever the exception) backs off and leaves the feed broken.</summary>
    async Task PollCoreAsync(string driveId, Feed feed, DateTimeOffset startedAt)
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
                var page = await CallAsync(ct => api.GetDeltaAsync(link, ct));
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
            var changed = Apply(driveId, feed, changes);
            feed.Backoff = feed.Pushed || !changed ? Math.Min(feed.Backoff + 1, 16) : 0;
            feed.DeltaLink = link;
            Recovered(driveId, feed, startedAt);
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
            catch (Exception again)
            {
                feed.DeltaLink = null;
                Failed(driveId, feed, again);
            }
        }
        catch (Exception e)
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
        feed.DeltaLink = await CallAsync(ct => api.GetLatestDeltaLinkAsync(driveId, ct));
        feed.BaselineAt = Now;
        Recovered(driveId, feed, feed.BaselineAt);
    }

    static readonly TimeSpan FirstFeedRetry = TimeSpan.FromMinutes(1), LastFeedRetry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A read failed. Listings fall back to ListingTtl (read again when opened) until one succeeds. Reads back off from 1 to 30
    /// minutes, give or take a quarter at random, and the log tells once per problem instead of on every attempt.
    /// </summary>
    void Failed(string driveId, Feed feed, Exception e)
    {
        TimeSpan wait;
        lock (feed)
        {
            feed.Broken = true;
            feed.Failures++;
            wait = TimeSpan.FromTicks(Math.Min(LastFeedRetry.Ticks, FirstFeedRetry.Ticks << Math.Min(feed.Failures - 1, 10))) * (0.75 + 0.5 * options.Random());
            feed.RetryAt = Now + wait;
        }
        var problem = e is RemoteException { Error: RemoteError.ReadOnly }
            ? "SharePoint has the library read-only for now (maintenance or a site move)"
            : e.Message;
        if (problem != feed.Problem)
        {
            Log($"change feed of {FeedName(driveId)}: {problem}. Folders are read again when opened; trying again in {wait.TotalMinutes:0} min, then less often");
            feed.Problem = problem;
        }
    }

    void Recovered(string driveId, Feed feed, DateTimeOffset readAt)
    {
        if (feed.Failures > 0)
        {
            Log($"change feed of {FeedName(driveId)} works again after {feed.Failures} failed read(s)");
        }
        lock (feed)
        {
            feed.LastRead = readAt;
            feed.Broken = false;
            feed.Failures = 0;
            feed.RetryAt = default;
        }
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
                    var fresh = new Listing(Now, new PriorityHolder());
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

    /// <summary>Loads a folder ahead of a walk, in the background unless the walker comes to wait for it.</summary>
    async Task PrefetchAsync(string driveId, string folderPath, Listing listing, Feed feed)
    {
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

    /// <summary>
    /// Applies changes read from the feed to the cached listings and lookups of a drive, reports them and keeps them for replays.
    /// True when a listing changed (echoes of this engine's own uploads change nothing).
    /// </summary>
    bool Apply(string driveId, Feed feed, IReadOnlyList<DriveChange> changes)
    {
        if (changes.Count == 0)
        {
            return false;
        }
        Log($"change feed of drive {driveId}: {changes.Count} change(s)");
        lock (feed.ApplyGate)
        {
            var at = Now;
            Record(feed, changes.Select(change => new Recorded(at, change, null, null)));
            return ApplyCore(driveId, changes, null);
        }
    }

    /// <summary>Keeps changes for replays; called under the feed's ApplyGate.</summary>
    void Record(Feed feed, IEnumerable<Recorded> changes)
    {
        feed.Recent.AddRange(changes);
        Trim(feed);
    }

    /// <summary>Lets go of changes older than RecentWindow, and of the oldest beyond MaxRecent; called under the feed's ApplyGate.</summary>
    void Trim(Feed feed)
    {
        var at = Now;
        var drop = feed.Recent.FindIndex(r => at - r.At <= RecentWindow);
        drop = Math.Max(drop < 0 ? feed.Recent.Count : drop, feed.Recent.Count - MaxRecent);
        if (drop > 0)
        {
            feed.DroppedUpTo = feed.Recent[drop - 1].At;
            feed.Recent.RemoveRange(0, drop);
        }
    }

    /// <summary>
    /// Called under the feed's ApplyGate when a listing finished loading or was read again: changes from the feed and local writes
    /// since it was asked for may be missing from it, so they are applied to it again, in order. When some of those were let go it is
    /// marked stale instead.
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
        var remote = new List<DriveChange>();
        foreach (var recorded in feed.Recent)
        {
            if (recorded.At < listing.RequestedAt)
            {
                continue;
            }
            if (recorded.Remote is { } change)
            {
                remote.Add(change);
                continue;
            }
            if (remote.Count > 0)
            {
                ApplyCore(driveId, remote, folderPath);
                remote = [];
            }
            if (ParentOf(recorded.Path!).Equals(folderPath, StringComparison.OrdinalIgnoreCase))
            {
                ApplyLocal(listing, recorded.Path!, recorded.Item, replay: true);
            }
        }
        if (remote.Count > 0)
        {
            ApplyCore(driveId, remote, folderPath);
        }
    }

    /// <summary>
    /// Applies changes to the drive's loaded listings (or only to <paramref name="onlyPath"/>'s) by item ID. A folder that moved, was
    /// renamed or deleted loses what is cached at and below its old path: found by its own listing, by the listing it is in, or by
    /// the listings right below it; so do two listings that claim the same folder ID (one of them is stale). A change older than the
    /// version a listing has (a read that started before one of this engine's uploads) is left out. True when a listing changed.
    /// </summary>
    bool ApplyCore(string driveId, IReadOnlyList<DriveChange> changes, string? onlyPath)
    {
        var prefix = Key(driveId, "");
        var all = listings.Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal) && l.Value.IsLoaded)
            .ToDictionary(l => l.Key[prefix.Length..], l => l.Value, StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, string>(StringComparer.Ordinal);
        var shared = new List<string>();
        // Listings whose parent folder isn't cached, by that folder's ID: a rename or delete of it is seen through them
        var below = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, listing) in all)
        {
            if (listing.FolderId is { } folderId && !folders.TryAdd(folderId, path))
            {
                shared.Add(folders[folderId]);
                shared.Add(path);
            }
            if (path.Contains('/') && listing.ParentId is { } parentId && !all.ContainsKey(ParentOf(path)))
            {
                (below.TryGetValue(parentId, out var children) ? children : below[parentId] = []).Add(path);
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
        // Item ID to the cached listing holding it, built on the first change not found where it is reported
        Dictionary<string, string>? holders = null;

        foreach (var change in changes)
        {
            if (change.Deleted && onlyPath is null)
            {
                content.ForgetItem(driveId, change.Id);
            }
            var item = change.Deleted ? null : change.Item;
            var newFolder = change.ParentId is { } parentId && folders.TryGetValue(parentId, out var folder) ? folder : null;

            // Where the item is cached now: usually in the folder it is reported in
            string? wasFolder = null, wasName = null;
            Listing? wasListing = null;
            if (newFolder is not null && cached.TryGetValue(newFolder, out var here) && here.ById.TryGetValue(change.Id, out var hereName))
            {
                (wasFolder, wasListing, wasName) = (newFolder, here, hereName);
            }
            else if (Holder(change.Id) is { } holder && cached.TryGetValue(holder, out var there) && there.ById.TryGetValue(change.Id, out var thereName))
            {
                (wasFolder, wasListing, wasName) = (holder, there, thereName);
            }
            var old = wasListing is not null && wasListing.Items.TryGetValue(wasName!, out var known) && known.Id == change.Id ? known : null;
            if (item is not null && old is not null && IsOlder(item.ETag, old.ETag))
            {
                continue; // read before a newer version was known here (this engine's own upload or rename)
            }

            if (folders.TryGetValue(change.Id, out var folderPath) && all.TryGetValue(folderPath, out var folderListing)
                && FolderMoved(folderPath, folderListing, item, change.ParentId))
            {
                // Reported below when its parent's listing still has it there; otherwise here
                var listedThere = all.TryGetValue(ParentOf(folderPath), out var parentListing) && parentListing.Items.TryGetValue(NameOf(folderPath), out var entry)
                    && entry.Id == change.Id;
                DropSubtree(folderPath);
                if (!listedThere)
                {
                    Report(driveId, folderPath, ChangeKind.Removed, true);
                }
            }
            if (below.TryGetValue(change.Id, out var children))
            {
                // The folder above these listings isn't cached: renamed or deleted, they are gone from their paths
                foreach (var gonePath in children.Select(ParentOf).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                {
                    if (item is null || !item.Name.Equals(NameOf(gonePath), StringComparison.OrdinalIgnoreCase))
                    {
                        below.Remove(change.Id);
                        var listedThere = all.TryGetValue(ParentOf(gonePath), out var aboveListing) && aboveListing.Items.TryGetValue(NameOf(gonePath), out var entry)
                            && entry.Id == change.Id;
                        DropSubtree(gonePath);
                        if (!listedThere)
                        {
                            Report(driveId, gonePath, ChangeKind.Removed, true);
                        }
                    }
                }
            }

            var moved = wasListing is not null && (item is null || newFolder is null || !wasFolder!.Equals(newFolder, StringComparison.OrdinalIgnoreCase) || wasName != item.Name);
            if (moved && old is not null)
            {
                var oldPath = JoinDrivePath(wasFolder!, wasName!);
                wasListing!.Remove(wasName!, out _);
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
            if (existing is not null && existing.Id == item.Id && ((existing.ETag == item.ETag && existing.Size == item.Size) || IsOlder(item.ETag, existing.ETag)))
            {
                continue; // already known (an echo of this engine's own upload), or older than what is known
            }
            target.Set(item);
            if (holders is not null)
            {
                holders[item.Id] = newFolder;
            }
            lookups.TryRemove(Key(driveId, itemPath), out _);
            Report(driveId, itemPath, existing is null ? ChangeKind.Added : ChangeKind.Modified, item.IsFolder);
            changed = true;
        }
        if (changed)
        {
            Interlocked.Increment(ref metadataVersion);
        }
        return changed;

        string? Holder(string id)
        {
            if (holders is null)
            {
                holders = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (path, listing) in cached)
                {
                    foreach (var itemId in listing.ById.Keys)
                    {
                        holders[itemId] = path;
                    }
                }
            }
            return holders.GetValueOrDefault(id);
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
    /// Graph eTags are "{stem},version" with one stem per item: true when <paramref name="incoming"/> is an earlier version of the same
    /// item than <paramref name="known"/>. Tags in another form never count as older.
    /// </summary>
    static bool IsOlder(string? incoming, string? known)
    {
        if (incoming is null || known is null)
        {
            return false;
        }
        var split = incoming.LastIndexOf(',');
        return split > 0 && known.LastIndexOf(',') == split && incoming.AsSpan(0, split).SequenceEqual(known.AsSpan(0, split))
            && long.TryParse(incoming.AsSpan(split + 1).Trim('"'), out var earlier) && long.TryParse(known.AsSpan(split + 1).Trim('"'), out var later)
            && earlier < later;
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
    /// Every 30 s (from <see cref="PollActiveAsync"/>): lookups past their TTL go, so do changes kept for replays past their time,
    /// and listings beyond MaxCachedItems, least recently used first (a folder counts as used when one below it is: the listings
    /// below rely on it to be kept current).
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
        foreach (var failed in unlisted)
        {
            if (Now - failed.Value >= UnlistedRetry)
            {
                unlisted.TryRemove(failed);
            }
        }
        foreach (var feed in feeds.Values)
        {
            lock (feed.ApplyGate)
            {
                Trim(feed);
            }
        }

        var size = listings.Values.Sum(l => (long)l.Items.Count + 1);
        if (size <= options.MaxCachedItems)
        {
            return;
        }
        var target = options.MaxCachedItems / 10 * 9;
        var evicted = 0;
        var loaded = listings.Where(l => l.Value.IsLoaded).ToList();
        var used = LastUseBelow(loaded);
        foreach (var old in loaded.Where(l => Now - used[l.Key] > ListingKeep).OrderBy(l => used[l.Key]).ThenByDescending(l => l.Key.Count(c => c == '/')).ToList())
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

    /// <summary>When each listing, or a cached one below it, was last used.</summary>
    static Dictionary<string, DateTimeOffset> LastUseBelow(IReadOnlyCollection<KeyValuePair<string, Listing>> cached)
    {
        var used = cached.ToDictionary(l => l.Key, l => l.Value.LastUsed, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, listing) in cached)
        {
            var drive = key[..(key.IndexOf('|') + 1)];
            for (var path = key[drive.Length..]; path.Length > 0;)
            {
                path = ParentOf(path);
                if (used.TryGetValue(drive + path, out var at) && at < listing.LastUsed)
                {
                    used[drive + path] = listing.LastUsed;
                }
            }
        }
        return used;
    }

    /// <summary>Lengths downloads served that differ from the listed size, per item, for the content version they were served for.</summary>
    readonly ConcurrentDictionary<string, (string? Tag, long Length)> servedLengths = new(StringComparer.Ordinal);

    /// <summary>
    /// A file's size as reads serve it: the cached content's length when that version is cached (also after a restart), else what a
    /// download served this run. Listings and lookups bring SharePoint's own figure back.
    /// </summary>
    long ServedSize(string driveId, DriveItemInfo item)
    {
        var tag = item.CTag ?? item.ETag;
        return content.LengthOf(driveId, item.Id, tag)
            ?? (servedLengths.TryGetValue(Key(driveId, item.Id), out var served) && served.Tag == tag ? served.Length : item.Size);
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
        if (feeds.TryGetValue(driveId, out var feed))
        {
            feed.EchoUntil = Now + NotifyEcho;
        }
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

    /// <summary>
    /// Listings the change feeds keep current, with the links to continue from; per drive the most recently used first (a folder
    /// before the ones below it, which rely on it).
    /// </summary>
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
            var cached = listings.Where(l => l.Key.StartsWith(prefix, StringComparison.Ordinal) && l.Value.IsLoaded).ToList();
            var used = LastUseBelow(cached);
            var folders = cached.Where(l => Maintained(driveId, l.Key[prefix.Length..], l.Value))
                .OrderByDescending(l => used[l.Key])
                .ThenBy(l => l.Key.Count(c => c == '/'))
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
    /// what changed since, and an expired link drops them. Lookups in them wait briefly for that read. They are read again only
    /// after MaintainedListingMaxAge from when they were fetched.
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
