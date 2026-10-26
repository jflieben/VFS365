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
/// stops it and says how to search instead. Woken by the tabs' navigation events and the drive's folder listings, idle otherwise; all
/// COM calls on its own thread. Never throws: problems are logged once.
/// </summary>
internal sealed partial class ExplorerSearchWatcher : IDisposable
{
    const int PollGap = 250, ListingPollGap = 1000, AwakeFor = 3000, MaxWindows = 64, OpenGap = 3000, OpensPerWindow = 10, CallLimit = 5000;
    const int OpensWindow = 10 * 60 * 1000, RestFor = 10 * 60 * 1000, HintGap = 10 * 60 * 1000;

    readonly DriveEngine engine;
    readonly ExplorerSearchMode mode;
    readonly Func<IReadOnlyList<string>> roots;
    readonly Func<string, string?> volumePath;
    readonly Func<string, string, Func<string, bool>?, CancellationToken, Task<string?>> handOff;
    readonly Action<string, string> notify;
    readonly Action<string> log;
    readonly AutoResetEvent wake = new(false);
    readonly CancellationTokenSource stop = new();
    readonly Thread thread;
    readonly HashSet<string> logged = [];
    readonly Queue<long> opens = new();
    readonly uint session = (uint)Process.GetCurrentProcess().SessionId;
    /// <summary>Searches already showing at the first look (windows Explorer restored at sign-in): stopped, never opened.</summary>
    readonly HashSet<(long Tab, string Key)> restored = [];
    long lastActivity, lastHint = -HintGap;
    int explorerSignalled;
    uint watcherThread;
    Timer? watchdog;

    ExplorerSearchWatcher(DriveEngine engine, ExplorerSearchMode mode, Func<IReadOnlyList<string>> roots, Func<string, string?> volumePath,
        Func<string, string, Func<string, bool>?, CancellationToken, Task<string?>> handOff, Action<string, string> notify, Action<string> log)
    {
        (this.engine, this.mode, this.roots, this.volumePath, this.handOff, this.notify, this.log) = (engine, mode, roots, volumePath, handOff, notify, log);
        engine.FolderListed += Woken;
        thread = new Thread(Run) { IsBackground = true, Name = "VFS365 Explorer search" };
        thread.SetApartmentState(ApartmentState.MTA); // calls into explorer.exe and its events need no message loop here
        thread.Start();
        WokenByExplorer(); // a first look now, so searches typed later are told apart from restored ones
    }

    /// <summary>Starts watching; null for mode Allow.</summary>
    public static ExplorerSearchWatcher? Start(DriveEngine engine, ExplorerSearchMode mode, Func<IReadOnlyList<string>> roots, Func<string, string?> volumePath,
        Func<string, string, Func<string, bool>?, CancellationToken, Task<string?>> handOff, Action<string, string> notify, Action<string> log) =>
        mode == ExplorerSearchMode.Allow ? null : new ExplorerSearchWatcher(engine, mode, roots, volumePath, handOff, notify, log);

    /// <summary>A folder listing on the drive: polls at most once per <see cref="ListingPollGap"/> ms (a copy lists many).</summary>
    void Woken()
    {
        Volatile.Write(ref lastActivity, Environment.TickCount64);
        wake.Set();
    }

    /// <summary>An Explorer event (navigation, a tab opened or closed): polls at once.</summary>
    void WokenByExplorer()
    {
        Volatile.Write(ref explorerSignalled, 1);
        Woken();
    }

    void Run()
    {
        object? shell = null;
        var connections = new Dictionary<long, IDisposable>();
        var previous = new Dictionary<long, string>();
        var settle = new SearchSettle();
        var sink = new WakeSink(WokenByExplorer);
        long lastPoll = 0, restUntil = 0;
        var failures = 0;
        var looked = false;
        // A hung Explorer must not keep this thread in a call: the watchdog cancels a poll's call after CallLimit ms
        _ = CoEnableCallCancellation(IntPtr.Zero);
        watcherThread = GetCurrentThreadId();
        using var watchdog = this.watchdog = new Timer(_ =>
        {
            _ = CoCancelCall(watcherThread, 0);
            Once($"Explorer search: Explorer didn't answer within {CallLimit / 1000} s; the call was cancelled");
        });

        void Reset()
        {
            foreach (var connection in connections.Values)
            {
                connection.Dispose();
            }
            connections.Clear();
            if (shell is not null)
            {
                try
                {
                    Marshal.FinalReleaseComObject(shell);
                }
                catch (ArgumentException)
                {
                }
            }
            shell = null;
        }

        while (!stop.IsCancellationRequested)
        {
            var now = Environment.TickCount64;
            var awake = settle.Pending || now - Volatile.Read(ref lastActivity) < AwakeFor;
            wake.WaitOne(awake ? PollGap : Timeout.Infinite);
            now = Environment.TickCount64;
            var byExplorer = Interlocked.Exchange(ref explorerSignalled, 0) == 1;
            if (stop.IsCancellationRequested || now < restUntil || !byExplorer && !settle.Pending && now - lastPoll < ListingPollGap)
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
                if (connecting)
                {
                    // Never start Explorer (a RemoteApp session has none): only connect to one that runs in this session
                    var running = Process.GetProcessesByName("explorer");
                    var here = running.Any(p => (uint)p.SessionId == session);
                    foreach (var process in running)
                    {
                        process.Dispose();
                    }
                    if (!here)
                    {
                        continue;
                    }
                }
                watchdog.Change(CallLimit, Timeout.Infinite);
                List<ExplorerWindows.Window> windows;
                try
                {
                    if (connecting)
                    {
                        shell = ExplorerWindows.Open();
                        connections[0] = ExplorerWindows.Connect<DShellWindowsEvents>(shell, sink);
                    }
                    windows = ExplorerWindows.List(shell!, MaxWindows).Where(w => w.SessionId == session).ToList();
                }
                finally
                {
                    watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                }
                if (connecting)
                {
                    log($"Explorer search: watching Explorer ({windows.Count} window(s) or tab(s))");
                }
                foreach (var gone in connections.Keys.Where(k => k != 0 && windows.All(w => w.Identity != k)).ToList())
                {
                    connections[gone].Dispose();
                    connections.Remove(gone);
                    previous.Remove(gone);
                    settle.See(gone, null, now);
                    restored.RemoveWhere(r => r.Tab == gone);
                }
                foreach (var window in windows)
                {
                    if (!connections.ContainsKey(window.Identity))
                    {
                        watchdog.Change(CallLimit, Timeout.Infinite);
                        try
                        {
                            connections[window.Identity] = ExplorerWindows.Connect<DWebBrowserEvents2>(window.Browser, sink);
                        }
                        finally
                        {
                            watchdog.Change(Timeout.Infinite, Timeout.Infinite);
                        }
                    }
                    Check(window, settle, previous, now, initial: !looked);
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

    /// <summary>
    /// One tab: remembers the folder it shows, or acts on a search on the drive once its words settled. <paramref name="initial"/>: the
    /// first look after the start, whose searches were restored by Explorer, not typed.
    /// </summary>
    void Check(ExplorerWindows.Window window, SearchSettle settle, Dictionary<long, string> previous, long now, bool initial)
    {
        var search = ExplorerSearch.FromView(window.ParsingName, window.LocationUrl, window.LocationName, roots(), previous.GetValueOrDefault(window.Identity));
        var location = search?.Locations.Count == 1 ? search.Locations[0] : null;
        var volume = location is null ? null : volumePath(location);
        if (search is null || location is null || volume is null || search.Terms.Length == 0)
        {
            if (search is null && window.ParsingName is { } shown && !shown.StartsWith("::", StringComparison.Ordinal))
            {
                previous[window.Identity] = shown;
            }
            settle.See(window.Identity, null, now); // no search, elsewhere, or the box was cleared
            restored.RemoveWhere(r => r.Tab == window.Identity);
            return;
        }
        var key = $"{volume}|{search.Terms}";
        if (initial)
        {
            restored.Add((window.Identity, key));
        }
        switch (settle.See(window.Identity, key, now))
        {
            case SearchSettle.Verdict.Act:
                var open = mode == ExplorerSearchMode.Browser && !restored.Contains((window.Identity, key)) && MayOpen(now);
                StopTab(window, location, first: true);
                log($"Explorer search: stopped a search in {volume} ({search.Terms.Length} characters){(open ? ", opening it in Microsoft 365" : "")}");
                if (open)
                {
                    var browser = window.Browser;
                    _ = Task.Run(async () =>
                    {
                        if (await handOff(volume, search.Terms, url => OpenThroughExplorer(browser, url), stop.Token).ConfigureAwait(false) is { } words)
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
                StopTab(window, location, first: false);
                log($"Explorer search: the search in {volume} still showed, stopped it again");
                break;
        }
    }

    /// <summary>Back to the folder searched (which ends Explorer's crawl); again: back in its history, then stop.</summary>
    void StopTab(ExplorerWindows.Window window, string location, bool first)
    {
        watchdog?.Change(CallLimit, Timeout.Infinite);
        try
        {
            if (first)
            {
                ExplorerWindows.Navigate(window.Browser, location);
                return;
            }
            ExplorerWindows.GoBack(window.Browser);
        }
        catch (Exception e) when (e is COMException or RuntimeBinderException)
        {
            Once($"Explorer search: couldn't send a tab back ({e.Message})");
            try
            {
                ExplorerWindows.Stop(window.Browser);
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

    /// <summary>Explorer's events only wake the watcher; it reads the tabs itself.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class WakeSink(Action woken) : DShellWindowsEvents, DWebBrowserEvents2
    {
        public void WindowRegistered(int cookie) => woken();

        public void WindowRevoked(int cookie) => woken();

        public void BeforeNavigate2(object browser, ref object url, ref object flags, ref object frame, ref object postData, ref object headers, ref bool cancel) => woken();

        public void NavigateComplete2(object browser, ref object url) => woken();

        public void DocumentComplete(object browser, ref object url) => woken();
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
