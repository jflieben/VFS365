using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

/// <summary>What happens to a search in Explorer's search box on the drive (policy ExplorerSearch).</summary>
public enum ExplorerSearchMode { Browser, Stop, Allow }

/// <summary>
/// Hands searches in Explorer's search box on the drive to Microsoft 365 (decision 48, spike 15). Explorer searches a folder by listing
/// every folder below it, so when a tab shows a search on the drive the watcher waits until the words stop changing (Explorer searches
/// as you type), sends the tab back to the folder, which ends the crawl, and opens SharePoint search with the same words. Mode Stop only
/// stops it and says how to search instead. Woken by the tabs' navigation events (which tab navigated tells it what to read), the
/// opening and closing of tabs and the drive's folder listings, idle otherwise; all COM calls on its own thread, every wrapper released
/// after use. Never throws: problems are logged once.
/// </summary>
internal sealed partial class ExplorerSearchWatcher : IDisposable
{
    const int PollGap = 250, ListingPollGap = 1000, AwakeFor = 3000, MaxWindows = 64, OpenGap = 3000, OpensPerWindow = 10, CallLimit = 5000;
    const int OpensWindow = 10 * 60 * 1000, RestFor = 10 * 60 * 1000, HintGap = 10 * 60 * 1000;

    readonly DriveEngine engine;
    readonly ExplorerSearchMode mode;
    readonly Func<IReadOnlyList<string>> roots;
    readonly Func<string, string, Func<string, bool>?, CancellationToken, Task<string?>> handOff;
    readonly Action<string, string> notify;
    readonly Action<string> log;
    readonly SearchTracker tracker;
    readonly AutoResetEvent wake = new(false);
    readonly CancellationTokenSource stop = new();
    readonly Thread thread;
    readonly HashSet<string> logged = [];
    readonly Queue<long> opens = new();
    readonly uint session = (uint)Process.GetCurrentProcess().SessionId;
    /// <summary>Tabs that navigated since the last pass (identities; the event sinks add, the watcher thread takes).</summary>
    readonly ConcurrentDictionary<long, byte> navigated = new();
    /// <summary>The File Explorer tabs of this session that are connected, by identity (the watcher thread only).</summary>
    readonly Dictionary<long, Tab> tabs = [];
    object? shell;
    IDisposable? shellEvents;
    long lastActivity, lastListing = -AwakeFor, lastHint = -HintGap;
    int refreshSignalled;
    uint watcherThread;
    Timer? watchdog;

    ExplorerSearchWatcher(DriveEngine engine, ExplorerSearchMode mode, Func<IReadOnlyList<string>> roots, Func<string, string?> volumePath,
        Func<string, string, Func<string, bool>?, CancellationToken, Task<string?>> handOff, Action<string, string> notify, Action<string> log)
    {
        (this.engine, this.mode, this.roots, this.handOff, this.notify, this.log) = (engine, mode, roots, handOff, notify, log);
        tracker = new SearchTracker(volumePath);
        engine.FolderListed += Woken;
        thread = new Thread(Run) { IsBackground = true, Name = "VFS365 Explorer search" };
        thread.SetApartmentState(ApartmentState.MTA); // calls into explorer.exe and its events need no message loop here
        thread.Start();
        WokenByExplorer(0); // a first look now, so searches typed later are told apart from restored ones
    }

    /// <summary>Starts watching; null for mode Allow.</summary>
    public static ExplorerSearchWatcher? Start(DriveEngine engine, ExplorerSearchMode mode, Func<IReadOnlyList<string>> roots, Func<string, string?> volumePath,
        Func<string, string, Func<string, bool>?, CancellationToken, Task<string?>> handOff, Action<string, string> notify, Action<string> log) =>
        mode == ExplorerSearchMode.Allow ? null : new ExplorerSearchWatcher(engine, mode, roots, volumePath, handOff, notify, log);

    /// <summary>A folder listing on the drive: looks at every tab, at most once per <see cref="ListingPollGap"/> ms (a copy lists many).</summary>
    void Woken()
    {
        Volatile.Write(ref lastListing, Environment.TickCount64);
        Volatile.Write(ref lastActivity, Environment.TickCount64);
        wake.Set();
    }

    /// <summary>An Explorer event: the tab with this identity navigated, or (0) a tab was opened or closed. Polls at once.</summary>
    void WokenByExplorer(long tab)
    {
        if (tab == 0)
        {
            Volatile.Write(ref refreshSignalled, 1);
        }
        else
        {
            navigated[tab] = 0;
        }
        Volatile.Write(ref lastActivity, Environment.TickCount64);
        wake.Set();
    }

    void Run()
    {
        long lastPoll = 0, lastSweep = 0, restUntil = 0;
        var failures = 0;
        var looked = false;
        // A hung Explorer must not keep this thread in a call: the watchdog cancels a call after CallLimit ms
        _ = CoEnableCallCancellation(IntPtr.Zero);
        watcherThread = GetCurrentThreadId();
        using var watchdog = this.watchdog = new Timer(_ =>
        {
            _ = CoCancelCall(watcherThread, 0);
            Once($"Explorer search: Explorer didn't answer within {CallLimit / 1000} s; the call was cancelled");
        });

        while (!stop.IsCancellationRequested)
        {
            var now = Environment.TickCount64;
            var awake = tracker.Pending || now - Volatile.Read(ref lastActivity) < AwakeFor;
            wake.WaitOne(awake ? PollGap : Timeout.Infinite);
            now = Environment.TickCount64;
            var refresh = Interlocked.Exchange(ref refreshSignalled, 0) == 1;
            // The drive lists folders: look at every tab now and then, in case an event was missed
            var sweep = now - lastSweep >= ListingPollGap && now - Volatile.Read(ref lastListing) < AwakeFor;
            if (stop.IsCancellationRequested || now < restUntil || !refresh && !sweep && navigated.IsEmpty && !tracker.Pending)
            {
                continue;
            }
            if (now - lastPoll < PollGap)
            {
                stop.Token.WaitHandle.WaitOne((int)(PollGap - (now - lastPoll)));
                now = Environment.TickCount64;
            }
            lastPoll = now;
            try
            {
                var connecting = shell is null;
                // Never start Explorer (a RemoteApp session has none): only connect to one that runs in this session
                if (connecting && !ExplorerRunsHere())
                {
                    continue;
                }
                if (sweep)
                {
                    lastSweep = now;
                }
                var added = connecting || refresh || sweep ? Refresh(connecting, now) : [];
                if (connecting)
                {
                    log($"Explorer search: watching Explorer ({tabs.Count} window(s) or tab(s))");
                }
                // Only the tabs that navigated, the new ones and those with a search settling; all of them on a sweep or at the start
                var look = new HashSet<long>(added.Select(t => t.Identity));
                look.UnionWith(tracker.PendingTabs);
                foreach (var id in navigated.Keys.ToList())
                {
                    navigated.TryRemove(id, out _);
                    look.Add(id);
                }
                if (connecting || sweep)
                {
                    look.UnionWith(tabs.Keys);
                }
                var places = look.Count > 0 ? roots() : [];
                foreach (var id in look)
                {
                    if (tabs.TryGetValue(id, out var tab))
                    {
                        Look(tab, places, now, initial: !looked);
                    }
                    else
                    {
                        tracker.Gone(id, now); // closed since
                    }
                }
                looked = true;
                failures = 0;
            }
            catch (Exception e) when (e is COMException or InvalidCastException or RuntimeBinderException or InvalidComObjectException or ArgumentException)
            {
                Once($"Explorer search: Explorer didn't answer ({e.Message}); trying again when the drive is used");
                Reset();
                if (++failures >= 5)
                {
                    restUntil = now + RestFor;
                    failures = 0;
                }
            }
        }
        Reset();
    }

    bool ExplorerRunsHere()
    {
        var running = Process.GetProcessesByName("explorer");
        var here = running.Any(p => (uint)p.SessionId == session);
        foreach (var process in running)
        {
            process.Dispose();
        }
        return here;
    }

    /// <summary>
    /// Compares Explorer's windows and tabs with the connected ones: connects the new tabs (File Explorer windows of this session), drops
    /// the closed ones. Opens the Shell Windows object first when <paramref name="connecting"/>. Returns the new tabs.
    /// </summary>
    List<Tab> Refresh(bool connecting, long now)
    {
        if (connecting)
        {
            Guarded(() =>
            {
                shell = ExplorerWindows.Open();
                shellEvents = ExplorerWindows.Connect<DShellWindowsEvents>(shell, new WakeSink(0, WokenByExplorer));
            });
        }
        var added = new List<Tab>();
        var present = new HashSet<long>();
        var items = Guarded(() => ExplorerWindows.Items(shell!, MaxWindows, line => Once($"Explorer search: {line}")));
        var next = 0;
        try
        {
            while (next < items.Count)
            {
                var item = items[next++];
                var kept = false;
                try
                {
                    var id = ExplorerWindows.IdentityOf(item);
                    present.Add(id);
                    if (tabs.ContainsKey(id))
                    {
                        continue;
                    }
                    var info = Guarded(() => ExplorerWindows.Identify(item));
                    if (info.SessionId != session || !ExplorerWindows.IsFileExplorer(info.ClassName))
                    {
                        continue;
                    }
                    var connection = Guarded(() => ExplorerWindows.Connect<DWebBrowserEvents2>(item, new WakeSink(id, WokenByExplorer)));
                    var tab = new Tab(item, id, connection);
                    tabs[id] = tab;
                    added.Add(tab);
                    kept = true;
                }
                catch (Exception e) when (TabFailed(e))
                {
                    Once($"Explorer search: couldn't connect to a tab ({e.Message})");
                }
                finally
                {
                    if (!kept)
                    {
                        ExplorerWindows.Release(item);
                    }
                }
            }
        }
        finally
        {
            for (; next < items.Count; next++)
            {
                ExplorerWindows.Release(items[next]);
            }
        }
        foreach (var gone in tabs.Values.Where(t => !present.Contains(t.Identity)).ToList())
        {
            Drop(gone, now);
        }
        return added;
    }

    /// <summary>
    /// A failed call into one tab, other than a call the watchdog cancelled (Explorer hanging: the caller resets): the tab closed, or
    /// belongs to a window this process may not use.
    /// </summary>
    static bool TabFailed(Exception e) =>
        e is COMException or InvalidCastException or RuntimeBinderException or InvalidComObjectException or UnauthorizedAccessException
        && !ExplorerWindows.Cancelled(e);

    /// <summary>A call into Explorer under the watchdog: cancelled when Explorer doesn't answer within <see cref="CallLimit"/> ms.</summary>
    T Guarded<T>(Func<T> call)
    {
        watchdog?.Change(CallLimit, Timeout.Infinite);
        try
        {
            return call();
        }
        finally
        {
            watchdog?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    void Guarded(Action call) => Guarded(() =>
    {
        call();
        return 0;
    });

    /// <summary>Disconnects and releases every tab and the Shell Windows object.</summary>
    void Reset()
    {
        shellEvents?.Dispose();
        shellEvents = null;
        foreach (var tab in tabs.Values.ToList())
        {
            Drop(tab, Environment.TickCount64);
        }
        ExplorerWindows.ReleaseAll(shell);
        shell = null;
    }

    void Drop(Tab tab, long now)
    {
        tabs.Remove(tab.Identity);
        tracker.Gone(tab.Identity, now);
        tab.Dispose();
    }

    /// <summary>
    /// One tab: remembers the folder it shows, or acts on a search on the drive once its words settled. <paramref name="initial"/>: the
    /// first look after the start, whose searches were restored by Explorer, not typed.
    /// </summary>
    void Look(Tab tab, IReadOnlyList<string> places, long now, bool initial)
    {
        ExplorerWindows.View view;
        try
        {
            view = Guarded(() => ExplorerWindows.ReadView(tab.Browser));
        }
        catch (Exception e) when (TabFailed(e))
        {
            Once($"Explorer search: couldn't read a tab ({e.Message})");
            Volatile.Write(ref refreshSignalled, 1); // it probably closed
            return;
        }
        if (tracker.Look(tab.Identity, view, places, now, initial) is not { } hit)
        {
            return;
        }
        var volume = hit.Volume;
        switch (hit.Verdict)
        {
            case SearchSettle.Verdict.Act:
                var open = mode == ExplorerSearchMode.Browser && !hit.Restored && MayOpen(now);
                StopTab(tab.Browser, hit.Location, first: true);
                log($"Explorer search: stopped a search in {volume} ({hit.Terms.Length} characters){(open ? ", opening it in Microsoft 365" : "")}");
                if (open)
                {
                    var browser = tab.Browser;
                    _ = Task.Run(async () =>
                    {
                        if (await handOff(volume, hit.Terms, url => OpenThroughExplorer(browser, url), stop.Token).ConfigureAwait(false) is { } words)
                        {
                            notify(UiText.Get("Search.HandedOff.Title"), UiText.Get("Search.HandedOff", words));
                        }
                    });
                }
                else if (mode == ExplorerSearchMode.Stop && now - lastHint >= HintGap)
                {
                    lastHint = now;
                    notify(UiText.Get("Search.Title"), UiText.Get("Search.Stopped"));
                }
                break;
            case SearchSettle.Verdict.StopAgain:
                StopTab(tab.Browser, hit.Location, first: false);
                log($"Explorer search: the search in {volume} still showed, stopped it again");
                break;
        }
    }

    /// <summary>Back to the folder searched (which ends Explorer's crawl); again: back in its history, then stop.</summary>
    void StopTab(object browser, string location, bool first)
    {
        watchdog?.Change(CallLimit, Timeout.Infinite);
        try
        {
            if (first)
            {
                ExplorerWindows.Navigate(browser, location);
                return;
            }
            ExplorerWindows.GoBack(browser);
        }
        catch (Exception e) when (e is COMException or RuntimeBinderException)
        {
            Once($"Explorer search: couldn't send a tab back ({e.Message})");
            try
            {
                ExplorerWindows.Stop(browser);
            }
            catch (Exception again) when (again is COMException or RuntimeBinderException)
            {
            }
        }
        finally
        {
            watchdog?.Change(Timeout.Infinite, Timeout.Infinite);
        }
    }

    /// <summary>At most one browser page per 3 s and ten per 10 minutes, whatever Explorer does.</summary>
    bool MayOpen(long now)
    {
        while (opens.Count > 0 && now - opens.Peek() >= OpensWindow)
        {
            opens.Dequeue();
        }
        if (opens.Count >= OpensPerWindow || opens.Count > 0 && now - opens.Last() < OpenGap)
        {
            Once("Explorer search: many searches in a short time; stopping them without opening more pages for now");
            return false;
        }
        opens.Enqueue(now);
        return true;
    }

    /// <summary>
    /// Opens the page through the tab's Explorer (<see cref="ExplorerWindows.ShellExecute"/>), so the browser may come to the front; false
    /// (the caller opens it itself) when Explorer refuses or doesn't answer within <see cref="CallLimit"/> ms.
    /// </summary>
    bool OpenThroughExplorer(object browser, string url)
    {
        try
        {
            var opening = Task.Run(() => ExplorerWindows.ShellExecute(browser, url));
            if (opening.Wait(CallLimit))
            {
                return true;
            }
            Once("Explorer search: Explorer didn't open the page in time; it opens without coming to the front");
        }
        catch (AggregateException e) when (e.InnerException is COMException or RuntimeBinderException or InvalidComObjectException or InvalidCastException)
        {
            Once($"Explorer search: Explorer couldn't open the page ({e.InnerException.Message}); it opens without coming to the front");
        }
        return false;
    }

    void Once(string line)
    {
        lock (logged)
        {
            if (!logged.Add(line))
            {
                return;
            }
        }
        log(line);
    }

    public void Dispose()
    {
        engine.FolderListed -= Woken;
        stop.Cancel();
        wake.Set();
        thread.Join(TimeSpan.FromSeconds(2)); // a hung Explorer call keeps the background thread, not the stop
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoEnableCallCancellation(IntPtr reserved);

    [LibraryImport("ole32.dll")]
    private static partial int CoCancelCall(uint threadId, uint timeout);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    /// <summary>A connected tab: its IWebBrowser2 wrapper and the connection of its events. Dispose ends both.</summary>
    sealed class Tab(object browser, long identity, IDisposable connection) : IDisposable
    {
        public object Browser { get; } = browser;

        public long Identity { get; } = identity;

        public void Dispose()
        {
            try
            {
                connection.Dispose();
            }
            finally
            {
                ExplorerWindows.ReleaseAll(Browser);
            }
        }
    }

    /// <summary>
    /// Explorer's events only wake the watcher; it reads the tabs itself. <paramref name="tab"/>: the identity of the tab the events come
    /// from (0: the Shell Windows object). Each event passes the browser it concerns, a new reference to a wrapper that is released here.
    /// </summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class WakeSink(long tab, Action<long> woken) : DShellWindowsEvents, DWebBrowserEvents2
    {
        public void WindowRegistered(int cookie) => woken(tab);

        public void WindowRevoked(int cookie) => woken(tab);

        // Navigate events come in threes; the other two follow once the new folder shows, so this one needs no read
        public void BeforeNavigate2(object browser, ref object url, ref object flags, ref object frame, ref object postData, ref object headers, ref bool cancel) =>
            ExplorerWindows.Release(browser);

        public void NavigateComplete2(object browser, ref object url)
        {
            woken(tab);
            ExplorerWindows.Release(browser);
        }

        public void DocumentComplete(object browser, ref object url)
        {
            woken(tab);
            ExplorerWindows.Release(browser);
        }
    }
}

/// <summary>A search to act on: the tab's search view settled (<see cref="SearchSettle.Verdict.Act"/>) or still shows (StopAgain).</summary>
internal sealed record SearchHit(SearchSettle.Verdict Verdict, string Volume, string Location, string Terms, bool Restored);

/// <summary>
/// What each tab shows, remembered tab by tab, and when a search on the drive is acted on. A search view names its own scope (see
/// <see cref="ExplorerSearch.FromView"/>); the folder the tab showed before only completes a name that is that folder's display name,
/// so a search in This PC, Home or Quick access is never taken for one in the folder shown before them. No Explorer calls.
/// </summary>
internal sealed class SearchTracker(Func<string, string?> volumePath)
{
    readonly SearchSettle settle = new();
    /// <summary>The last folder each tab showed (not a search): its parsing name and display name.</summary>
    readonly Dictionary<long, (string? Parsing, string? Name)> shown = [];
    /// <summary>Searches already showing at the first look (windows Explorer restored at sign-in): stopped, never opened.</summary>
    readonly HashSet<(long Tab, string Key)> restored = [];

    public bool Pending => settle.Pending;

    /// <summary>The tabs with a search settling.</summary>
    public IReadOnlyList<long> PendingTabs => [.. settle.PendingTabs];

    /// <summary>
    /// One look at a tab: remembers the folder it shows, or tells what to do about a search on the drive. Null: nothing yet.
    /// <paramref name="roots"/>: the drive's roots. <paramref name="initial"/>: the first look after the start.
    /// </summary>
    public SearchHit? Look(long tab, ExplorerWindows.View view, IEnumerable<string> roots, long now, bool initial)
    {
        shown.TryGetValue(tab, out var before);
        var search = ExplorerSearch.FromView(view.ParsingName, view.LocationUrl, view.LocationName, roots, before.Parsing, before.Name);
        var location = search?.Locations.Count == 1 ? search.Locations[0] : null;
        var volume = location is null ? null : volumePath(location);
        if (search is null || location is null || volume is null || search.Terms.Length == 0)
        {
            if (search is null && view.ParsingName is { } folder)
            {
                shown[tab] = (folder, view.LocationName);
            }
            settle.See(tab, null, now); // no search, elsewhere, or the box was cleared
            restored.RemoveWhere(r => r.Tab == tab);
            return null;
        }
        var key = $"{volume}|{search.Terms}";
        if (initial)
        {
            restored.Add((tab, key));
        }
        var verdict = settle.See(tab, key, now);
        return verdict is SearchSettle.Verdict.Act or SearchSettle.Verdict.StopAgain
            ? new SearchHit(verdict, volume, location, search.Terms, restored.Contains((tab, key)))
            : null;
    }

    /// <summary>The tab closed (or was dropped): forgets it.</summary>
    public void Gone(long tab, long now)
    {
        shown.Remove(tab);
        settle.See(tab, null, now);
        restored.RemoveWhere(r => r.Tab == tab);
    }
}

/// <summary>
/// When a search shown in a tab is handed over. Explorer searches as you type (spike 15), so a search acts once its words and folder
/// stayed the same for <see cref="Settle"/> ms, or <see cref="MaxWait"/> ms after its first words at the latest. The same search still
/// showing <see cref="Recheck"/> to <see cref="Repeat"/> ms later, without the tab having left it (sending it back didn't work), is
/// only stopped again; once the tab showed something else, the same words again are a new search. Times in milliseconds.
/// </summary>
internal sealed class SearchSettle
{
    public const int Settle = 1500, MaxWait = 6000, Recheck = 2000, Repeat = 15000;

    public enum Verdict { Wait, Act, StopAgain, None }

    readonly Dictionary<long, (string Key, long First, long Changed)> tabs = [];
    readonly Dictionary<(long Tab, string Key), long> done = [];

    public bool Pending => tabs.Count > 0;

    /// <summary>The tabs whose search is still settling.</summary>
    public IEnumerable<long> PendingTabs => tabs.Keys;

    /// <summary>What a tab shows now: a search's key (folder and words), or null for anything else.</summary>
    public Verdict See(long tab, string? key, long now)
    {
        foreach (var old in done.Where(d => now - d.Value >= Repeat || key is null && d.Key.Tab == tab).Select(d => d.Key).ToList())
        {
            done.Remove(old); // too old, or the tab left the search: sending it back worked
        }
        if (key is null)
        {
            tabs.Remove(tab);
            return Verdict.None;
        }
        if (done.TryGetValue((tab, key), out var acted))
        {
            tabs.Remove(tab);
            if (now - acted < Recheck)
            {
                return Verdict.None; // the tab may still be on its way back
            }
            done[(tab, key)] = now;
            return Verdict.StopAgain;
        }
        if (!tabs.TryGetValue(tab, out var seen))
        {
            seen = (key, now, now);
        }
        else if (seen.Key != key)
        {
            seen = (key, seen.First, now);
        }
        tabs[tab] = seen;
        if (now - seen.Changed < Settle && now - seen.First < MaxWait)
        {
            return Verdict.Wait;
        }
        tabs.Remove(tab);
        done[(tab, key)] = now;
        return Verdict.Act;
    }
}
