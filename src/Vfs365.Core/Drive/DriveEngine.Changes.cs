using System.Collections.Concurrent;

namespace Vfs365.Core.Drive;

/// <summary>A cached folder listing as kept between runs. FetchedAt and ParentId are missing in files written by older versions.</summary>
public sealed record FolderSnapshot(string Path, string FolderId, IReadOnlyList<DriveItemInfo> Items, DateTimeOffset? FetchedAt = null, string? ParentId = null);

/// <summary>A folder above cached listings known only by its ID and its parent's ID (never listed), as kept between runs.</summary>
public sealed record AncestorSnapshot(string Path, string FolderId, string? ParentId);

/// <summary>A drive's cached listings with the delta link that continues from the moment they reflect. Ancestors are missing in older files.</summary>
public sealed record DriveSnapshot(string DriveId, string DeltaLink, DateTimeOffset SavedAt, IReadOnlyList<FolderSnapshot> Folders, IReadOnlyList<AncestorSnapshot>? Ancestors = null);

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

        /// <summary>
        /// The first push notification not read yet, of a drive nobody looked at lately: the next use reads first, and a drive still in
        /// use at the latest PushLazyRead after it.
        /// </summary>
        public DateTimeOffset? NotifiedAt { get; set; }
        public Task? Polling { get; set; }

        /// <summary>When the running read started (Environment.TickCount64): lookups wait for it a few seconds from then at most.</summary>
        public long PollStartedTick { get; set; }

        /// <summary>
        /// A read that failed after some pages were applied goes on from ResumeLink (the next page), counting ResumePages already read;
        /// the round started at RoundStartedAt.
        /// </summary>
        public string? ResumeLink { get; set; }
        public int ResumePages { get; set; }
        public DateTimeOffset RoundStartedAt { get; set; }

        /// <summary>Counts delta link changes worth saving (at most hourly): part of <see cref="MetadataVersion"/>.</summary>
        public long LinkGeneration { get; set; }
        public DateTimeOffset LinkCountedAt { get; set; }

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

        /// <summary>
        /// Item ID to the path of the cached listing holding it, kept between reads under ApplyGate (it may be out of date: every use
        /// checks the listing); Indexed is the listing version each listing was indexed at.
        /// </summary>
        public Dictionary<string, string> Holders { get; } = new(StringComparer.Ordinal);
        public Dictionary<Listing, int> Indexed { get; } = [];

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

    /// <summary>A folder above cached listings known by its ID and its parent's ID as of At, so the feed sees it renamed or moved.</summary>
    sealed record KnownFolder(string Id, string? ParentId, DateTimeOffset At);

    readonly ConcurrentDictionary<string, Feed> feeds = new(StringComparer.Ordinal);

    /// <summary>Folders above cached listings that were looked up instead of listed (<see cref="TrackAncestors"/>), by key.</summary>
    readonly ConcurrentDictionary<string, KnownFolder> knownFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders being looked up by <see cref="TrackAncestors"/>.</summary>
    readonly ConcurrentDictionary<string, byte> tracking = new(StringComparer.OrdinalIgnoreCase);

    static Feed NewFeed() => new();

    /// <summary>How long an opened folder counts as part of a walk going down the tree.</summary>
    static readonly TimeSpan OpenedWindow = TimeSpan.FromMinutes(2);

    /// <summary>How long after reporting changes the opens that follow count as Explorer reading them again.</summary>
    static readonly TimeSpan NotifyEcho = TimeSpan.FromSeconds(3);

    /// <summary>The read after a push channel came up waits up to this long at random, and is left out when a read started less than this before.</summary>
    static readonly TimeSpan PushCatchUp = TimeSpan.FromSeconds(30);

    /// <summary>A push notification for a drive nobody looked at lately is read at the latest this long after, while the drive is in use.</summary>
    static readonly TimeSpan PushLazyRead = TimeSpan.FromMinutes(5);

    /// <summary>Feeds backing off after failures are read again within this long at random after the network returns.</summary>
    static readonly TimeSpan NetworkJitter = TimeSpan.FromSeconds(30);

    /// <summary>A lookup in a listing whose feed wasn't read lately waits this long at most for the read it started.</summary>
    static readonly TimeSpan FeedCatchUpWait = TimeSpan.FromSeconds(3);

    /// <summary>A catch-up still running after FeedCatchUpWait leaves listings current only when the last read is younger than this.</summary>
    static readonly TimeSpan TrustedCatchUp = TimeSpan.FromHours(1);

    /// <summary>A delta link that moved on counts as a change worth saving at most this often.</summary>
    static readonly TimeSpan LinkSaveInterval = TimeSpan.FromHours(1);

    /// <summary>
    /// The drive's change feed keeps the listing current: it has its folder ID, was asked for after the feed's baseline, and every
    /// folder above it is cached and lists it, or is known by ID (<see cref="TrackAncestors"/>) as its parent. A rename or move of such
    /// a folder drops what is cached below it; one of a folder that isn't known would go unnoticed, so below such a folder listings
    /// are only trusted for ListingTtl.
    /// </summary>
    bool Maintained(string driveId, string folderPath, Listing listing)
    {
        if (!feeds.TryGetValue(driveId, out var feed))
        {
            return false;
        }
        var (folderId, parentId, at) = (listing.FolderId, listing.ParentId, listing.RequestedAt);
        while (true)
        {
            if (folderId is null || !feed.Maintains(at))
            {
                return false;
            }
            if (!folderPath.Contains('/'))
            {
                return true; // its parent is the drive's root, which is never renamed or moved
            }
            var parentPath = ParentOf(folderPath);
            if (listings.TryGetValue(Key(driveId, parentPath), out var parent) && parent.IsLoaded)
            {
                if (!parent.ById.TryGetValue(folderId, out var name) || !name.Equals(NameOf(folderPath), StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                (folderId, parentId, at) = (parent.FolderId, parent.ParentId, parent.RequestedAt);
            }
            else if (parentId is not null && knownFolders.TryGetValue(Key(driveId, parentPath), out var known) && known.Id == parentId)
            {
                (folderId, parentId, at) = (known.Id, known.ParentId, known.At);
            }
            else
            {
                return false;
            }
            folderPath = parentPath;
        }
    }

    /// <summary>
    /// The feed was read lately: within two background intervals, or, with push up since before that read, within the pushed safety
    /// interval (a change would have been pushed). Not after a notification that wasn't read yet.
    /// </summary>
    bool FeedCurrent(string driveId) =>
        feeds.TryGetValue(driveId, out var feed) && feed.NotifiedAt is null
        && Now - feed.LastRead < (feed.Pushed && feed.LastRead >= feed.PushedAt ? options.PushedPollInterval : options.BackgroundPollInterval * 2);

    /// <summary>
    /// After waiting for a catch-up: listings it maintains count as current when the read is done (FeedCurrent then says so), or the
    /// last read is recent. Not after a long time away (a catch-up of hundreds of pages, a restored snapshot a week old) while the
    /// catch-up still runs or can't run.
    /// </summary>
    bool CaughtUp(string driveId) => feeds.TryGetValue(driveId, out var feed) && Now - feed.LastRead < TrustedCatchUp;

    /// <summary>Reads the drive's changes or joins the read running, waiting FeedCatchUpWait from when that read started at most.</summary>
    async Task CatchUpAsync(string driveId)
    {
        // A read that finished since the caller looked (one its use started) needs no other: it counts before its task ends
        var read = FeedCurrent(driveId) ? Task.CompletedTask : PollAsync(driveId);
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
        if (feed.NotifiedAt is not null || Now - feed.LastPoll >= UseInterval(feed))
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
    /// A push notification for the drive (already debounced). Read now while someone works in the drive (used within HotWindow), or
    /// PushGap after the last read when that started less than PushGap ago. Otherwise the feed only counts as behind: the next use
    /// reads first (lookups wait for that briefly), and a drive still in use is read PushLazyRead later at the latest. A library
    /// colleagues co-author in all day then costs reads only while it is looked at.
    /// </summary>
    public Task NotifiedAsync(string driveId)
    {
        var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
        lock (feed)
        {
            if (HotPollingPaused || Now - feed.LastUsed >= options.HotWindow)
            {
                feed.NotifiedAt ??= Now;
                return Task.CompletedTask;
            }
            if (Now - feed.LastPoll < PushGap)
            {
                feed.ReadSoon = true;
                return Task.CompletedTask;
            }
        }
        return PollAsync(driveId);
    }

    /// <summary>
    /// Reads a drive's changes now, or joins the read already running. Not before the back-off ends after failures, nor while background
    /// requests are held after throttling (<see cref="HotPollingPaused"/>): the client would hold the read until the engine gives up on
    /// it. The drive's feed runs from now on (the drive counts as active only when used).
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
            if (Now < feed.RetryAt || HotPollingPaused)
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
    /// reads after a push channel came up, push reads that waited for PushGap and notifications left for later.
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
            if (Now - feed.LastUsed >= options.ActiveWindow)
            {
                return false;
            }
            return since >= Interval(feed) || feed.NotifiedAt is { } notified && Now - notified >= PushLazyRead;
        }
    }

    TimeSpan Interval(Feed feed) =>
        HotPolling && Now - feed.LastUsed < options.HotWindow ? HotInterval(feed)
        : feed.Pushed ? options.PushedPollInterval
        : options.BackgroundPollInterval;

    long pausedTicks = DateTimeOffset.MinValue.UtcTicks;

    /// <summary>
    /// Set by the host (every second) while the client holds background requests after throttling. Meanwhile no feed is read (also
    /// no hot checks and no push reads; notifications wait), and a read that fails while the client held it doesn't count against
    /// the feed. Off by itself when HotPollInterval is zero only for hot checks.
    /// </summary>
    public bool HotPollingPaused
    {
        get => Volatile.Read(ref paused);
        set
        {
            if (value)
            {
                Volatile.Write(ref pausedTicks, Now.UtcTicks);
            }
            Volatile.Write(ref paused, value);
        }
    }

    bool paused;

    /// <summary>Background requests were held at some point since <paramref name="since"/>: a failure meanwhile may be the hold's.</summary>
    bool PausedSince(DateTimeOffset since) => HotPollingPaused || Volatile.Read(ref pausedTicks) >= since.UtcTicks;

    bool HotPolling => options.HotPollInterval > TimeSpan.Zero && !HotPollingPaused;

    /// <summary>Drives used within ActiveWindow.</summary>
    public IReadOnlyList<string> ActiveDrives() => feeds.Where(f => Now - f.Value.LastUsed < options.ActiveWindow).Select(f => f.Key).ToList();

    /// <summary>Drives used within ActiveWindow whose change feed works: they get push channels (while a feed fails, a push reads nothing).</summary>
    public IReadOnlyList<string> PushDrives() => feeds.Where(f => Now - f.Value.LastUsed < options.ActiveWindow && !f.Value.Broken).Select(f => f.Key).ToList();

    /// <summary>
    /// The network is back: feeds backing off after failures are read again soon, each 0 to 30 s from now at random, and lookups ask
    /// Microsoft 365 again at once.
    /// </summary>
    public void NetworkRestored()
    {
        unreachable.Clear();
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

    /// <summary>
    /// A read that fails or gets no answer within CallTimeout (whatever the exception) backs off and leaves the feed broken, unless
    /// the client held it (a throttling pause). Each page is applied as it comes, so a read that fails midway goes on from the next
    /// page instead of from the start.
    /// </summary>
    async Task PollCoreAsync(string driveId, Feed feed, DateTimeOffset startedAt)
    {
        RequestPriority.MarkBackground();
        try
        {
            if (feed.DeltaLink is not { } start)
            {
                await BaselineAsync(driveId, feed);
                return;
            }
            var resumed = feed.ResumeLink is not null;
            var link = feed.ResumeLink ?? start;
            var pages = feed.ResumePages;
            var roundStart = resumed ? feed.RoundStartedAt : startedAt;
            var (changed, count) = (false, 0);
            while (true)
            {
                var page = await CallAsync(ct => api.GetDeltaAsync(link, ct));
                pages++;
                count += page.Changes.Count;
                changed |= Apply(driveId, feed, page.Changes);
                if (page.NextLink is { } next)
                {
                    if (pages >= CatchUpPages && pages > CachedListings(driveId))
                    {
                        Log($"change feed of {FeedName(driveId)} is more than {pages} pages behind; starting over, folders are read again");
                        DropDrive(driveId);
                        await BaselineAsync(driveId, feed);
                        return;
                    }
                    (feed.ResumeLink, feed.ResumePages, feed.RoundStartedAt) = (next, pages, roundStart);
                    link = next;
                    continue;
                }
                link = page.DeltaLink ?? link;
                break;
            }
            if (count > 0)
            {
                Log($"change feed of drive {driveId}: {count} change(s){(resumed ? " (a read that failed, continued)" : "")}");
            }
            feed.Backoff = feed.Pushed || !changed ? Math.Min(feed.Backoff + 1, 16) : 0;
            if (link != start && Now - feed.LinkCountedAt >= LinkSaveInterval)
            {
                (feed.LinkGeneration, feed.LinkCountedAt) = (feed.LinkGeneration + 1, Now);
            }
            (feed.DeltaLink, feed.ResumeLink, feed.ResumePages) = (link, null, 0);
            Recovered(driveId, feed, roundStart);
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
                Failed(driveId, feed, again, startedAt);
            }
        }
        catch (Exception e)
        {
            Failed(driveId, feed, e, startedAt);
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
        (feed.ResumeLink, feed.ResumePages) = (null, 0);
        feed.DeltaLink = await CallAsync(ct => api.GetLatestDeltaLinkAsync(driveId, ct));
        feed.BaselineAt = Now;
        (feed.LinkGeneration, feed.LinkCountedAt) = (feed.LinkGeneration + 1, Now);
        Recovered(driveId, feed, feed.BaselineAt);
    }

    static readonly TimeSpan FirstFeedRetry = TimeSpan.FromMinutes(1), LastFeedRetry = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A read failed. Listings fall back to ListingTtl (read again when opened) until one succeeds. Reads back off from 1 to 30
    /// minutes, give or take a quarter at random, and the log tells once per problem instead of on every attempt. A read the client
    /// held after throttling (<see cref="HotPollingPaused"/> since it started) fails nothing: it is read again after the pause.
    /// </summary>
    void Failed(string driveId, Feed feed, Exception e, DateTimeOffset startedAt)
    {
        if (PausedSince(startedAt))
        {
            const string held = "held back while Microsoft 365 throttles";
            if (feed.Problem != held)
            {
                Log($"change feed of {FeedName(driveId)}: {held} ({e.Message}); read again after the pause");
                feed.Problem = held;
            }
            return;
        }
        if (Unanswered(e))
        {
            Unreachable(driveId);
        }
        TimeSpan wait;
        lock (feed)
        {
            feed.Broken = true;
            feed.Failures++;
            wait = TimeSpan.FromTicks(Math.Min(LastFeedRetry.Ticks, FirstFeedRetry.Ticks << Math.Min(feed.Failures - 1, 10))) * (0.75 + 0.5 * options.Random());
            feed.RetryAt = Now + wait;
        }
        var problem = e is RemoteException { Error: RemoteError.ReadOnly } ? ReadOnlyCause : e.Message;
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
            if (feed.NotifiedAt <= readAt)
            {
                feed.NotifiedAt = null;
            }
        }
        feed.Problem = null;
        Reachable(driveId);
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

    /// <summary>
    /// Looks up the folders above a listed one that aren't cached, in the background (an app opening files by path lists only the
    /// folder it opens): one item read each instead of listing them, which can be pages for a big folder. Only with their IDs can the
    /// change feed see a rename or move above it and keep the listing current without reading it again.
    /// </summary>
    void TrackAncestors(string driveId, string folderPath)
    {
        for (var path = ParentOf(folderPath); path.Length > 0; path = ParentOf(path))
        {
            var key = Key(driveId, path);
            if (listings.ContainsKey(key) || knownFolders.ContainsKey(key) || (unlisted.TryGetValue(key, out var failed) && Now - failed < UnlistedRetry)
                || !tracking.TryAdd(key, 0))
            {
                continue;
            }
            _ = TrackAsync(driveId, path, key);
        }
    }

    async Task TrackAsync(string driveId, string folderPath, string key)
    {
        await Task.Yield();
        RequestPriority.MarkBackground();
        try
        {
            // A recent lookup of it (Explorer resolving each folder of the path) answers without a call
            var (folder, at) = lookups.TryGetValue(key, out var looked) && looked.Item is { IsFolder: true, ParentId: not null } && Now - looked.At < options.LookupTtl
                ? (looked.Item, looked.At)
                : (await CallAsync(ct => api.GetItemAsync(driveId, folderPath, ct)), Now);
            if (folder is not { IsFolder: true })
            {
                unlisted[key] = Now;
                return;
            }
            var feed = feeds.GetOrAdd(driveId, _ => NewFeed());
            lock (feed.ApplyGate)
            {
                // Not when something changed it or a folder above it since it was read, or what did may have been let go
                if (at > feed.DroppedUpTo && !feed.Recent.Any(r => r.At >= at && (r.Remote?.Id == folder.Id
                    || r.Path is { } changed && (folderPath.Equals(changed, StringComparison.OrdinalIgnoreCase) || folderPath.StartsWith(changed + "/", StringComparison.OrdinalIgnoreCase)))))
                {
                    knownFolders[key] = new KnownFolder(folder.Id, folder.ParentId, at);
                }
            }
        }
        catch (Exception e)
        {
            if (Unanswered(e))
            {
                Unreachable(driveId);
            }
            unlisted[key] = Now;
        }
        finally
        {
            tracking.TryRemove(key, out _);
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
        lock (feed.ApplyGate)
        {
            var at = Now;
            Record(feed, changes.Select(change => new Recorded(at, change, null, null)));
            return ApplyCore(driveId, feed, changes, null);
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
    /// Called under the feed's ApplyGate before a listing that finished loading, or was read again, is taken as loaded: changes from
    /// the feed and local writes since it was asked for may be missing from it, so they are applied to it again, in order. False when
    /// some of those were let go: it is to be marked stale instead.
    /// </summary>
    bool Replay(string driveId, string folderPath, Listing listing)
    {
        if (!feeds.TryGetValue(driveId, out var feed))
        {
            return true;
        }
        if (listing.RequestedAt <= feed.DroppedUpTo)
        {
            return false;
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
                ApplyCore(driveId, feed, remote, (folderPath, listing));
                remote = [];
            }
            if (ParentOf(recorded.Path!).Equals(folderPath, StringComparison.OrdinalIgnoreCase))
            {
                ApplyLocal(listing, recorded.Path!, recorded.Item, replay: true);
            }
        }
        if (remote.Count > 0)
        {
            ApplyCore(driveId, feed, remote, (folderPath, listing));
        }
        return true;
    }

    /// <summary>
    /// Applies changes to the drive's loaded listings (or only to <paramref name="only"/>, a listing not taken as loaded yet) by item
    /// ID. A folder that moved, was renamed or deleted loses what is cached at and below its old path: found by its own listing, by
    /// the listing it is in, by the listings right below it, or as a folder known by ID; so do two listings that claim the same folder
    /// ID (one of them is stale). A change older than the version a listing has (a read that started before one of this engine's
    /// uploads) is left out, and so is a folder's new eTag and size after a change below it. Lookups of changed items go, also where
    /// their folder isn't cached. True when a listing changed. Called under the feed's ApplyGate.
    /// </summary>
    bool ApplyCore(string driveId, Feed feed, IReadOnlyList<DriveChange> changes, (string Path, Listing Listing)? only)
    {
        var prefix = Key(driveId, "");
        var all = new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>(); // every listing of the drive, loading ones too: what a dropped subtree takes along
        foreach (var (key, listing) in listings)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                paths.Add(key[prefix.Length..]);
                if (listing.IsLoaded)
                {
                    all[paths[^1]] = listing;
                }
            }
        }
        if (only is { } replayed)
        {
            all[replayed.Path] = replayed.Listing;
        }
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
        // Folders above listings known only by ID, by that ID
        var known = new Dictionary<string, (string Path, KnownFolder Folder)>(StringComparer.Ordinal);
        foreach (var (key, folder) in knownFolders)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal) && !all.ContainsKey(key[prefix.Length..]))
            {
                known.TryAdd(folder.Id, (key[prefix.Length..], folder));
            }
        }
        // Listings whose parent folder is neither cached nor known, by that folder's ID: a rename or delete of it is seen through them
        var below = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (path, listing) in all)
        {
            if (path.Contains('/') && listing.ParentId is { } parentId && !all.ContainsKey(ParentOf(path)) && !known.ContainsKey(parentId))
            {
                (below.TryGetValue(parentId, out var children) ? children : below[parentId] = []).Add(path);
            }
        }
        var cached = only is not { } single ? all
            : all.TryGetValue(single.Path, out var listed) ? new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase) { [single.Path] = listed }
            : new Dictionary<string, Listing>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        string[]? sorted = null;
        var dropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in shared.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            Log($"two cached listings of drive {driveId} claim one folder ID; '{path}' is read again");
            DropSubtree(path);
        }
        // Item ID to the cached listing holding it: for the first change not found where it is reported
        Dictionary<string, string>? holders = null;
        // Lookups of this drive by item ID, and folders known from lookups by ID: for changes outside cached listings
        Dictionary<string, List<string>>? lookedUp = null;

        foreach (var change in changes)
        {
            if (change.Deleted && only is null)
            {
                content.ForgetItem(driveId, change.Id);
            }
            var item = change.Deleted ? null : change.Item;
            var newFolder = change.ParentId is { } parentId && folders.TryGetValue(parentId, out var folder) ? folder : null;
            if (only is null && !lookups.IsEmpty)
            {
                ForgetLookups(change, item, newFolder);
            }

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
            var old = wasListing is not null && wasListing.Items.TryGetValue(wasName!, out var listedAs) && listedAs.Id == change.Id ? listedAs : null;
            if (item is not null && old is not null && IsOlder(item.ETag, old.ETag))
            {
                continue; // read before a newer version was known here (this engine's own upload or rename)
            }

            if (folders.TryGetValue(change.Id, out var folderPath) && all.TryGetValue(folderPath, out var folderListing)
                && FolderMoved(folderPath, folderListing.ParentId, item, change.ParentId, folderListing))
            {
                // Reported below when its parent's listing still has it there; otherwise here
                DropSubtree(folderPath);
                if (!ListedIn(ParentOf(folderPath), NameOf(folderPath), change.Id))
                {
                    Report(driveId, folderPath, ChangeKind.Removed, true);
                }
            }
            else if (known.TryGetValue(change.Id, out var above) && !dropped.Contains(above.Path)
                && FolderMoved(above.Path, above.Folder.ParentId, item, change.ParentId, null))
            {
                DropSubtree(above.Path);
                if (!ListedIn(ParentOf(above.Path), NameOf(above.Path), change.Id))
                {
                    Report(driveId, above.Path, ChangeKind.Removed, true);
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
                        DropSubtree(gonePath);
                        if (!ListedIn(ParentOf(gonePath), NameOf(gonePath), change.Id))
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
            if (existing is { IsFolder: true } && item.IsFolder && existing.Id == item.Id && existing.Name == item.Name
                && existing.Modified == item.Modified && existing.Created == item.Created)
            {
                target.Set(item, quiet: true); // a change below it gave it a new eTag and size: nothing Windows shows
                continue;
            }
            target.Set(item);
            SeenVersion(item);
            if (holders is not null)
            {
                holders[item.Id] = newFolder;
            }
            lookups.TryRemove(Key(driveId, itemPath), out _);
            Report(driveId, itemPath, existing is null ? ChangeKind.Added : ChangeKind.Modified, item.IsFolder);
            changed = true;
        }
        if (dropped.Count > 0)
        {
            ForgetBelow(dropped);
        }
        return changed;

        string? Holder(string id)
        {
            if (holders is null)
            {
                if (only is null)
                {
                    holders = HolderIndex(feed, cached);
                }
                else
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
            }
            return holders.GetValueOrDefault(id);
        }

        bool ListedIn(string parentPath, string name, string id) =>
            all.TryGetValue(parentPath, out var parent) && parent.Items.TryGetValue(name, out var entry) && entry.Id == id;

        // Is the folder at folderPath somewhere else now (or gone)? Only the name and, where known, the parent tell: a change to anything
        // inside a folder also reports the folder itself.
        bool FolderMoved(string folderPath, string? knownParentId, DriveItemInfo? item, string? parentId, Listing? listing)
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
            var expected = all.TryGetValue(ParentOf(folderPath), out var parent) && parent.FolderId is { } id ? id : knownParentId;
            if (expected is null || parentId is null)
            {
                if (listing is not null)
                {
                    listing.ParentId ??= parentId;
                }
                return false;
            }
            return expected != parentId;
        }

        // Drops the listings at and below a path, here and in the maps this pass uses; the lookups and known folders below go at the end
        void DropSubtree(string path)
        {
            changed = true;
            if (!dropped.Add(path))
            {
                return;
            }
            sorted ??= [.. paths.Order(StringComparer.OrdinalIgnoreCase)];
            foreach (var gone in AtAndBelow(sorted, path))
            {
                listings.TryRemove(Key(driveId, gone), out _);
                if (all.Remove(gone, out var listing) && listing.FolderId is { } id && folders.GetValueOrDefault(id) is { } mapped && mapped.Equals(gone, StringComparison.OrdinalIgnoreCase))
                {
                    folders.Remove(id);
                }
                cached.Remove(gone);
            }
        }

        // One sweep for all the subtrees this pass dropped
        void ForgetBelow(HashSet<string> roots)
        {
            foreach (var map in new[] { lookups.Keys, knownFolders.Keys })
            {
                foreach (var key in map)
                {
                    if (!key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    for (var path = key[prefix.Length..]; ; path = ParentOf(path))
                    {
                        if (roots.Contains(path))
                        {
                            lookups.TryRemove(key, out _);
                            knownFolders.TryRemove(key, out _);
                            break;
                        }
                        if (path.Length == 0)
                        {
                            break;
                        }
                    }
                }
            }
        }

        // Lookups of the item (by path elsewhere, its folder not cached) and, when its folder was looked up, of the name it has now
        void ForgetLookups(DriveChange change, DriveItemInfo? item, string? newFolder)
        {
            if (lookedUp is null)
            {
                lookedUp = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var (key, lookup) in lookups)
                {
                    if (lookup.Item is { } found && key.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        (lookedUp.TryGetValue(found.Id, out var keys) ? keys : lookedUp[found.Id] = []).Add(key);
                    }
                }
            }
            if (lookedUp.TryGetValue(change.Id, out var at))
            {
                foreach (var key in at)
                {
                    if (lookups.TryGetValue(key, out var lookup) && lookup.Item is { } was
                        && (item is null || was.ETag != item.ETag || !was.Name.Equals(item.Name, StringComparison.Ordinal) || (was.ParentId is not null && was.ParentId != change.ParentId)))
                    {
                        lookups.TryRemove(key, out _);
                    }
                }
            }
            if (item is not null && newFolder is null && change.ParentId is { } parentId && lookedUp.TryGetValue(parentId, out var parents))
            {
                foreach (var key in parents)
                {
                    lookups.TryRemove(Key(driveId, JoinDrivePath(key[prefix.Length..], item.Name)), out _);
                }
            }
        }
    }

    /// <summary>The paths of a sorted (case-insensitive ordinal) list at and below <paramref name="path"/> ("" for all).</summary>
    static IEnumerable<string> AtAndBelow(string[] sorted, string path)
    {
        if (path.Length == 0)
        {
            foreach (var all in sorted)
            {
                yield return all;
            }
            yield break;
        }
        if (Array.BinarySearch(sorted, path, StringComparer.OrdinalIgnoreCase) is var at and >= 0)
        {
            yield return sorted[at];
        }
        // Paths starting with "path/" are next to each other in this order
        var under = path + "/";
        var i = Array.BinarySearch(sorted, under, StringComparer.OrdinalIgnoreCase);
        for (i = i < 0 ? ~i : i; i < sorted.Length && sorted[i].StartsWith(under, StringComparison.OrdinalIgnoreCase); i++)
        {
            yield return sorted[i];
        }
    }

    /// <summary>
    /// The drive's item ID to listing index, brought up to date for listings that changed since the last read (or are new). Entries
    /// of listings that went may linger; each use checks the listing. Rebuilt when it holds far more than what is cached.
    /// </summary>
    static Dictionary<string, string> HolderIndex(Feed feed, Dictionary<string, Listing> cached)
    {
        var total = 0;
        foreach (var listing in cached.Values)
        {
            total += listing.Items.Count;
        }
        if (feed.Holders.Count > 2 * total + 1000 || feed.Indexed.Count > 2 * cached.Count + 100)
        {
            feed.Holders.Clear();
            feed.Indexed.Clear();
        }
        foreach (var (path, listing) in cached)
        {
            var version = listing.Version;
            if (feed.Indexed.TryGetValue(listing, out var indexed) && indexed == version)
            {
                continue;
            }
            foreach (var itemId in listing.ById.Keys)
            {
                feed.Holders[itemId] = path;
            }
            feed.Indexed[listing] = version;
        }
        return feed.Holders;
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
    /// What a listing costs in memory beyond its items, counted as this many items for MaxCachedItems (measured: about 1 KB per listing
    /// against about 300 bytes per item).
    /// </summary>
    const int ListingOverhead = 3;

    /// <summary>
    /// Every 30 s (from <see cref="PollActiveAsync"/>): lookups past their TTL go, so do changes kept for replays past their time,
    /// entries kept for scans of folders not used lately, folders known by ID with nothing cached below them any more, and listings
    /// beyond MaxCachedItems, least recently used first (a folder counts as used when one below it is: the listings below rely on it
    /// to be kept current).
    /// </summary>
    void Maintain()
    {
        if (Now < nextMaintenance)
        {
            return;
        }
        nextMaintenance = Now + MaintenanceInterval;
        foreach (var lookup in lookups)
        {
            if (Now - lookup.Value.At >= (lookup.Value.Item is null ? options.NotFoundTtl : options.LookupTtl))
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
        if (!knownFolders.IsEmpty)
        {
            var above = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in listings.Keys)
            {
                var split = key.IndexOf('|') + 1;
                for (var path = key[split..]; path.Length > 0;)
                {
                    path = ParentOf(path);
                    if (!above.Add(key[..split] + path))
                    {
                        break;
                    }
                }
            }
            foreach (var known in knownFolders)
            {
                if (!above.Contains(known.Key))
                {
                    knownFolders.TryRemove(known);
                }
            }
        }

        var size = 0L;
        foreach (var listing in listings.Values)
        {
            size += listing.Items.Count + ListingOverhead;
            if (listing.HasEntries && Now - listing.LastUsed > ListingKeep)
            {
                listing.DropEntries();
            }
        }
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
                size -= old.Value.Items.Count + ListingOverhead;
                evicted++;
            }
        }
        if (evicted > 0)
        {
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
            else if (old.Id != now.Id || (now.IsFolder ? old.Name != now.Name || old.Modified != now.Modified : old.ETag != now.ETag || old.Size != now.Size))
            {
                Report(driveId, JoinDrivePath(folderPath, now.Name), ChangeKind.Modified, now.IsFolder);
            }
        }
    }

    /// <summary>Forgets everything cached for a drive and tells the front end its top changed.</summary>
    void DropDrive(string driveId)
    {
        ForgetDrive(driveId);
        if (ns.VolumePathOf(driveId) is { } top)
        {
            options.Changed?.Invoke(new EngineChange(top, ChangeKind.Modified, true));
        }
    }

    /// <summary>Forgets everything cached for a drive: listings, lookups, folders known by ID, changes kept for replays.</summary>
    void ForgetDrive(string driveId)
    {
        var prefix = Key(driveId, "");
        foreach (var (key, _) in listings)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                listings.TryRemove(key, out _);
            }
        }
        foreach (var (key, _) in lookups)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                lookups.TryRemove(key, out _);
            }
        }
        foreach (var (key, _) in knownFolders)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
            {
                knownFolders.TryRemove(key, out _);
            }
        }
        if (feeds.TryGetValue(driveId, out var feed))
        {
            lock (feed.ApplyGate)
            {
                feed.Recent.Clear();
                feed.Holders.Clear();
                feed.Indexed.Clear();
            }
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

    /// <summary>
    /// Replaces the libraries (discovery finished or changed) and reports the virtual folders whose content changed. What is cached
    /// for libraries no longer shown goes.
    /// </summary>
    public void SetLibraries(IReadOnlyList<Discovery.LibraryEntry> libraries)
    {
        foreach (var path in ns.SetLibraries(libraries))
        {
            options.Changed?.Invoke(new EngineChange(path, ChangeKind.Modified, true));
        }
        foreach (var driveId in feeds.Keys)
        {
            if (ns.VolumePathOf(driveId) is null)
            {
                ForgetDrive(driveId);
                feeds.TryRemove(driveId, out _);
            }
        }
    }

    /// <summary>
    /// Changes when what <see cref="ExportSnapshots"/> would save changes: a listing the feeds keep current came, went or changed (not
    /// for a folder's new eTag after a change below it), or a feed's delta link moved on (counted at most hourly) or broke. The host
    /// saves the snapshot only when it differs from the version it saved last.
    /// </summary>
    public long MetadataVersion
    {
        get
        {
            var sum = 0L;
            foreach (var (key, listing) in listings)
            {
                var split = key.IndexOf('|');
                if (listing.IsLoaded && Maintained(key[..split], key[(split + 1)..], listing))
                {
                    sum += Mix(listing.Serial, listing.Version);
                }
            }
            foreach (var (driveId, feed) in feeds)
            {
                if (feed.DeltaLink is not null)
                {
                    sum += Mix(StringComparer.Ordinal.GetHashCode(driveId), feed.LinkGeneration * 2 + (feed.Broken ? 1 : 0));
                }
            }
            return sum;
        }
    }

    /// <summary>A 64-bit mix of two numbers (the finalizer of MurmurHash3), so sums of them rarely collide.</summary>
    static long Mix(long a, long b)
    {
        var x = (ulong)a * 0x9E3779B97F4A7C15UL ^ (ulong)b;
        x ^= x >> 33;
        x *= 0xFF51AFD7ED558CCDUL;
        x ^= x >> 33;
        x *= 0xC4CEB9F34A1D9D9BUL;
        x ^= x >> 33;
        return (long)x;
    }

    /// <summary>
    /// Listings the change feeds keep current, with the links to continue from; per drive the most recently used first (a folder
    /// before the ones below it, which rely on it). Drives no longer mounted are left out.
    /// </summary>
    public IReadOnlyList<DriveSnapshot> ExportSnapshots(int maxFoldersPerDrive)
    {
        var snapshots = new List<DriveSnapshot>();
        foreach (var (driveId, feed) in feeds)
        {
            if (feed.DeltaLink is not { } link || feed.Broken || ns.VolumePathOf(driveId) is null)
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
                .Select(l => new FolderSnapshot(l.Key[prefix.Length..], l.Value.FolderId!, l.Value.Items.Values is IReadOnlyList<DriveItemInfo> copy ? copy : [.. l.Value.Items.Values],
                    l.Value.FetchedAt, l.Value.ParentId))
                .ToList();
            if (folders.Count > 0)
            {
                var ancestors = knownFolders.Where(k => k.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(k => new AncestorSnapshot(k.Key[prefix.Length..], k.Value.Id, k.Value.ParentId))
                    .ToList();
                snapshots.Add(new DriveSnapshot(driveId, link, Now, folders, ancestors.Count > 0 ? ancestors : null));
            }
        }
        return snapshots;
    }

    /// <summary>
    /// Restores listings from an earlier run. They answer at once; the first read of each drive's feed (on first use) applies
    /// what changed since, and an expired link drops them. Lookups in them wait briefly for that read (and don't take them as current
    /// while a long catch-up runs). They are read again only after MaintainedListingMaxAge from when they were fetched. A restored
    /// drive counts as used only once it is used for real.
    /// </summary>
    public void ImportSnapshots(IEnumerable<DriveSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            feeds[snapshot.DriveId] = new Feed { DeltaLink = snapshot.DeltaLink, BaselineAt = DateTimeOffset.MinValue, LastUsed = DateTimeOffset.MinValue };
            foreach (var folder in snapshot.Folders)
            {
                var listing = Listing.Loaded(folder.Items, DateTimeOffset.MinValue, folder.FetchedAt ?? snapshot.SavedAt, folder.FolderId);
                listing.ParentId = folder.ParentId;
                listings.TryAdd(Key(snapshot.DriveId, folder.Path), listing);
            }
            foreach (var ancestor in snapshot.Ancestors ?? [])
            {
                knownFolders.TryAdd(Key(snapshot.DriveId, ancestor.Path), new KnownFolder(ancestor.FolderId, ancestor.ParentId, DateTimeOffset.MinValue));
            }
        }
    }
}
