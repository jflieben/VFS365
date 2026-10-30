using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Spike 15 (private/SPIKES.md), a hidden diagnostic. `search-spike windows`: what Explorer's windows show while someone searches on
/// the drive, optionally stopping such a window, with the crawl's listings per second from a mount log. `search-spike urls`: the scope
/// of a folder and the Microsoft 365 search pages that could show its results.
/// </summary>
internal static class SearchSpike
{
    public const string Usage = """
        Usage:
          vfs365 search-spike windows [--drive M:] [--seconds 600] [--interval 500] [--log <mount log>] [--events] [--cancel]
                                      [--act goback|navigate|stop] [--settle <ms>]
          vfs365 search-spike urls <folder on the drive (M:\Sites\... or \Sites\...)> <words> [--open <id>]
          vfs365 search-spike query <KQL>                  what SharePoint search (REST) and Graph search find for it
          vfs365 search-spike menu <folder>                whether test verbs (with and without AppliesTo) show on its background
        """;

    /// <summary>
    /// `search-spike menu`: writes two temporary HKCU verbs on folder backgrounds (one with AppliesTo for the folder, one without),
    /// builds the folder's background context menu offscreen (IShellFolder.CreateViewObject) and its own item menu, prints which test
    /// verbs show, and removes the verbs again.
    /// </summary>
    public static int Menu(string[] args, Action<string> say)
    {
        if (args.Length < 3 || !Directory.Exists(args[2]))
        {
            say(Usage);
            return 2;
        }
        var folder = Path.GetFullPath(args[2]).TrimEnd('\\') + @"\";
        var keys = new List<string>();
        void Verb(string kind, string name, string? appliesTo)
        {
            var path = $@"Software\Classes\{kind}\shell\{name}";
            keys.Add(path);
            using var menu = Registry.CurrentUser.CreateSubKey(path);
            menu.SetValue("MUIVerb", name);
            menu.SetValue("SubCommands", "");
            if (appliesTo is not null)
            {
                menu.SetValue("AppliesTo", appliesTo);
            }
            using var verb = menu.CreateSubKey(@"shell\01test");
            verb.SetValue("MUIVerb", name + " item");
            using var command = verb.CreateSubKey("command");
            command.SetValue("", "cmd.exe /c exit");
        }
        var applies = ExplorerVerbs.AppliesTo([Path.GetDirectoryName(folder.TrimEnd('\\'))!.TrimEnd('\\') + @"\"]); // a root above it, as M:\ is
        try
        {
            Verb(@"Directory\Background", "VFS365SpikeBackgroundApplies", applies);
            Verb(@"Directory\Background", "VFS365SpikeBackgroundPlain", null);
            string[] background = [];
            var thread = new Thread(() => background = ShellMenus.Background(folder));
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            say($"AppliesTo:  {applies}");
            say($"Background: {string.Join(" | ", background)}");
            foreach (var name in new[] { "VFS365SpikeBackgroundApplies", "VFS365SpikeBackgroundPlain" })
            {
                say($"{name}: {(background.Contains(name) ? "shown" : "NOT shown")} on the background");
            }
            return 0;
        }
        finally
        {
            foreach (var key in keys)
            {
                Registry.CurrentUser.DeleteSubKeyTree(key, false);
            }
        }
    }

    static readonly Lock output = new();

    /// <summary>`search-spike query`: the paths SharePoint's REST search and Graph's search find for a KQL query (25 at most each).</summary>
    public static async Task<int> QueryAsync(string[] args, SharePointDiscoveryApi api, M365Client client, Action<string> say)
    {
        var kql = string.Join(' ', args.Skip(2).Where(a => !a.StartsWith("--", StringComparison.Ordinal)));
        if (kql.Length == 0)
        {
            say(Usage);
            return 2;
        }
        var root = (await api.GetMyHomeAsync(default)).TenantRoot;
        say($"KQL:        {kql}");
        try
        {
            var rest = new Uri($"{root}/_api/search/query?querytext='{Uri.EscapeDataString(kql.Replace("'", "''"))}'&rowlimit=25&trimduplicates=false" +
                "&selectproperties='Path,ParentLink,IsDocument'&clienttype='VFS365'");
            using var doc = await client.GetJsonAsync(rest, default);
            var results = doc!.RootElement.GetProperty("PrimaryQueryResult").GetProperty("RelevantResults");
            say($"SharePoint: {results.GetProperty("TotalRows")} rows");
            foreach (var row in results.GetProperty("Table").GetProperty("Rows").EnumerateArray())
            {
                var cells = row.GetProperty("Cells").EnumerateArray().ToDictionary(c => c.GetProperty("Key").GetString() ?? "", c => c.GetProperty("Value").ToString());
                say($"  {cells.GetValueOrDefault("Path")}  (parent {cells.GetValueOrDefault("ParentLink")})");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            say($"SharePoint: failed: {e.Message}");
        }
        try
        {
            var body = System.Text.Json.JsonSerializer.Serialize(new { requests = new[] { new { entityTypes = new[] { "driveItem" }, query = new { queryString = kql }, size = 25 } } });
            using var doc = await client.SendJsonAsync(HttpMethod.Post, new Uri("https://graph.microsoft.com/v1.0/search/query"),
                () => new StringContent(body, Encoding.UTF8, "application/json"), default);
            foreach (var container in doc!.RootElement.GetProperty("value")[0].GetProperty("hitsContainers").EnumerateArray())
            {
                say($"Graph:      {(container.TryGetProperty("total", out var total) ? total.ToString() : "?")} hits");
                if (container.TryGetProperty("hits", out var hits))
                {
                    foreach (var hit in hits.EnumerateArray())
                    {
                        say($"  {hit.GetProperty("resource").GetProperty("webUrl").GetString()}");
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            say($"Graph:      failed: {e.Message}");
        }
        return 0;
    }

    public static int Windows(string[] args, AgentSettings settings, Action<string> say)
    {
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        bool Flag(string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

        var act = Option("--act")?.ToLowerInvariant();
        if (act is not (null or "goback" or "navigate" or "stop"))
        {
            say(Usage);
            return 2;
        }
        var unc = @"\" + AgentCommands.UncPrefix(Environment.UserName);
        var letter = Letter(Option("--drive") ?? settings.DriveLetter);
        var folders = FolderLetters(unc);
        var navTarget = NavigationPaneTarget();
        var roots = new Roots(unc, letter, folders, navTarget);
        void Say(string line)
        {
            lock (output)
            {
                say($"{DateTime.Now:HH:mm:ss.fff} {line}");
            }
        }
        Say($"roots: unc {unc}, letter {letter ?? "(none; --drive M:)"}, folder letters {(folders.Count == 0 ? "none" : string.Join(", ", folders.Select(f => $"{f.Key}={f.Value}")))}, " +
            $"navigation pane target {navTarget ?? "(not registered)"}");

        var stop = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Set();
        };
        var seconds = int.TryParse(Option("--seconds"), out var s) && s > 0 ? s : 600;
        var interval = int.TryParse(Option("--interval"), out var ms) && ms >= 50 ? ms : 500;
        var settle = int.TryParse(Option("--settle"), out var wait) && wait >= 0 ? wait : 0;
        var result = 0;
        // MTA: calls into explorer.exe and event callbacks need no message loop here
        var thread = new Thread(() => result = Watch(roots, seconds, interval, settle, act, Flag("--events"), Flag("--cancel"), Option("--log"), stop, Say))
        {
            IsBackground = true,
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return result;
    }

    sealed record Roots(string Unc, string? Letter, IReadOnlyDictionary<string, string> Folders, string? NavTarget)
    {
        public string? VolumePath(string location) =>
            ExplorerVerbs.VolumePath(ExplorerSearch.Unshell(location, ExplorerLocation.Clsid, NavTarget), Unc, Letter, Folders);

        public IReadOnlyList<string> All => ExplorerMenu.Roots(Unc, Letter, Folders.Keys);
    }

    static int Watch(Roots roots, int seconds, int interval, int settle, string? act, bool events, bool cancel, string? logPath,
        ManualResetEventSlim stop, Action<string> say)
    {
        object shellWindows;
        try
        {
            shellWindows = ExplorerWindows.Open();
        }
        catch (Exception e) when (e is COMException or TypeLoadException or ArgumentException)
        {
            say($"Shell Windows unavailable: {e.Message}");
            return 1;
        }
        var connections = new List<IDisposable>();
        var hooked = new HashSet<long>();
        if (events)
        {
            try
            {
                connections.Add(ExplorerWindows.Connect<DShellWindowsEvents>(shellWindows, new ShellWindowsSink(say)));
                say("events: connected to Shell Windows (WindowRegistered, WindowRevoked)");
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                say($"events: Shell Windows refused: {e.Message}");
            }
        }
        var tail = logPath is null ? null : new LogTail(logPath);
        if (logPath is not null)
        {
            say($"crawl: counting listings in {logPath} (lines 'fs readdir ... marker=-' and Graph 'GET .../children' per second)");
        }

        var clock = Stopwatch.StartNew();
        var lastTable = "";
        var printed = new HashSet<string>();
        var pending = new Dictionary<long, (string Raw, TimeSpan Since)>();
        var previous = new Dictionary<long, string>();
        var acted = new HashSet<string>();
        var checks = new List<(TimeSpan At, object Browser, long Identity, string Label)>();
        var nextPoll = TimeSpan.Zero;
        var nextBeat = TimeSpan.FromSeconds(10);
        var lastCount = 0;
        long lastPoll = 0;
        while (!stop.IsSet && clock.Elapsed.TotalSeconds < seconds)
        {
            if (clock.Elapsed >= nextPoll)
            {
                nextPoll = clock.Elapsed + TimeSpan.FromMilliseconds(interval);
                var timer = Stopwatch.StartNew();
                List<ExplorerWindows.Window> windows;
                try
                {
                    windows = ExplorerWindows.List(shellWindows, 64, problem => say($"problem: {problem}"));
                }
                catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
                {
                    say($"Shell Windows failed ({e.Message}); opening it again");
                    Thread.Sleep(1000);
                    try
                    {
                        shellWindows = ExplorerWindows.Open();
                    }
                    catch (COMException again)
                    {
                        say($"Shell Windows unavailable: {again.Message}");
                    }
                    continue;
                }
                lastPoll = timer.ElapsedMilliseconds;
                lastCount = windows.Count;
                var table = string.Join('\n', windows.Select(Row));
                if (table != lastTable)
                {
                    say($"windows: {windows.Count}, read in {lastPoll} ms");
                    foreach (var row in table.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        say($"  {row}");
                    }
                    lastTable = table;
                }
                foreach (var window in windows)
                {
                    if (events && hooked.Add(window.Identity))
                    {
                        try
                        {
                            connections.Add(ExplorerWindows.Connect<DWebBrowserEvents2>(window.Browser,
                                new BrowserSink(window.Identity, say, cancel ? url => ExplorerSearch.FromView(url, null, null, roots.All, null) is { } found && OnDrive(found, roots) : null)));
                            say($"events: connected to tab {window.Identity:X}");
                        }
                        catch (Exception e) when (e is COMException or InvalidCastException)
                        {
                            say($"events: tab {window.Identity:X} refused: {e.Message}");
                        }
                    }
                    if (ExplorerSearch.FromView(window.ParsingName, window.LocationUrl, window.LocationName, roots.All, previous.GetValueOrDefault(window.Identity))
                        is not { } search || window.ParsingName is not { } raw)
                    {
                        if (window.ParsingName is { } shown && !shown.StartsWith("::", StringComparison.Ordinal))
                        {
                            previous[window.Identity] = shown;
                        }
                        pending.Remove(window.Identity);
                        continue;
                    }
                    if (printed.Add($"{window.Identity}|{raw}"))
                    {
                        Describe(window, raw, search, roots, say);
                    }
                    if (act is null || !OnDrive(search, roots))
                    {
                        continue;
                    }
                    if (!pending.TryGetValue(window.Identity, out var seen) || seen.Raw != raw)
                    {
                        pending[window.Identity] = seen = (raw, clock.Elapsed);
                    }
                    if (clock.Elapsed - seen.Since >= TimeSpan.FromMilliseconds(settle) && acted.Add($"{window.Identity}|{raw}"))
                    {
                        Act(window, search, act, clock.Elapsed - seen.Since, say);
                        checks.Add((clock.Elapsed + TimeSpan.FromSeconds(1), window.Browser, window.Identity, "1 s"));
                        checks.Add((clock.Elapsed + TimeSpan.FromSeconds(3), window.Browser, window.Identity, "3 s"));
                    }
                }
            }
            foreach (var check in checks.Where(c => clock.Elapsed >= c.At).ToList())
            {
                checks.Remove(check);
                try
                {
                    var after = ExplorerWindows.ParsingName(check.Browser) ?? (string?)((dynamic)check.Browser).LocationURL;
                    say($"after {check.Label}: tab {check.Identity:X} shows {after ?? "(nothing)"}");
                }
                catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException)
                {
                    say($"after {check.Label}: {e.Message}");
                }
            }
            tail?.Pump(say);
            if (clock.Elapsed >= nextBeat)
            {
                nextBeat = clock.Elapsed + TimeSpan.FromSeconds(10);
                say($"heartbeat: {lastCount} windows, last read {lastPoll} ms");
            }
            stop.Wait(25);
        }
        foreach (var connection in connections)
        {
            connection.Dispose();
        }
        tail?.Pump(say, final: true);
        say("done");
        return 0;
    }

    static string Row(ExplorerWindows.Window w) =>
        $"tab {w.Identity:X} hwnd {w.Hwnd.ToInt64():X} pid {w.ProcessId} session {w.SessionId} class {w.ClassName} | url {w.LocationUrl ?? "-"} | name {w.LocationName ?? "-"} | parsing {w.ParsingName ?? "-"}";

    static bool OnDrive(ExplorerSearch search, Roots roots) => search.Locations.Count > 0 && search.Locations.All(l => roots.VolumePath(l) is not null);

    static void Describe(ExplorerWindows.Window window, string raw, ExplorerSearch search, Roots roots, Action<string> say)
    {
        say($"SEARCH in tab {window.Identity:X} (pid {window.ProcessId}):");
        say($"  raw:      {raw} | name {window.LocationName ?? "-"} | url {window.LocationUrl ?? "-"}");
        if (raw.StartsWith("search-ms:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var part in raw["search-ms:".Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                say($"  part:     {part}  =>  {Unescape(part)}");
            }
        }
        say($"  terms:    [{search.Terms}]  cleaned [{SearchTerms.Clean(search.Terms)}]  filters {(search.HasFilters ? "yes" : "no")}");
        foreach (var location in search.Locations)
        {
            say($"  location: {location}  =>  volume path {roots.VolumePath(location) ?? "(not on the drive)"}");
        }
    }

    static void Act(ExplorerWindows.Window window, ExplorerSearch search, string act, TimeSpan waited, Action<string> say)
    {
        try
        {
            switch (act)
            {
                case "goback":
                    ExplorerWindows.GoBack(window.Browser);
                    break;
                case "navigate":
                    ExplorerWindows.Navigate(window.Browser, search.Locations[0]);
                    break;
                default:
                    ExplorerWindows.Stop(window.Browser);
                    break;
            }
            say($"ACTED: {act} on tab {window.Identity:X}, {waited.TotalMilliseconds:0} ms after the search was first seen");
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            say($"ACT FAILED: {act} on tab {window.Identity:X}: {e.Message}");
        }
    }

    static string Unescape(string text)
    {
        try
        {
            return Uri.UnescapeDataString(text);
        }
        catch (UriFormatException)
        {
            return text;
        }
    }

    /// <summary>"M" or "M:" as "M:"; null for None, * or nothing.</summary>
    static string? Letter(string? value) =>
        value?.Trim().TrimEnd(':', '\\') is { Length: 1 } single && char.IsAsciiLetter(single[0]) ? char.ToUpperInvariant(single[0]) + ":" : null;

    /// <summary>The session's letters for folders of this volume ("Z:" to \\VFS365\ann\...).</summary>
    static Dictionary<string, string> FolderLetters(string unc)
    {
        var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var c = 'D'; c <= 'Z'; c++)
        {
            if (DosDevices.Target($"{c}:") is { } target && SessionDriveLetters.Ours(target, unc))
            {
                folders[$"{c}:"] = @"\" + target[@"\??\UNC".Length..];
            }
        }
        return folders;
    }

    static string? NavigationPaneTarget()
    {
        using var bag = Registry.CurrentUser.OpenSubKey($@"Software\Classes\CLSID\{ExplorerLocation.Clsid}\Instance\InitPropertyBag");
        return bag?.GetValue("TargetFolderPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
    }

    /// <summary>
    /// `search-spike urls`: the scope of a folder on the drive (one or two Graph GETs) and the candidate search pages, each with an ID;
    /// --open &lt;id&gt; opens the first whose ID starts with it.
    /// </summary>
    public static async Task<int> UrlsAsync(string[] args, AgentSettings settings, SharePointDiscoveryApi api, M365Client client, Action<string> say)
    {
        if (args.Length < 4)
        {
            say(Usage);
            return 2;
        }
        string? open = null;
        var words = new List<string>();
        for (var i = 3; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (args[i] is "--open" or "--config" && i + 1 < args.Length)
                {
                    open = args[i] == "--open" ? args[i + 1] : open;
                    i++;
                }
                continue;
            }
            words.Add(args[i]);
        }
        var unc = @"\" + AgentCommands.UncPrefix(Environment.UserName);
        var path = args[2];
        var volumePath = path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length >= 2 && path[1] == ':'
            ? ExplorerVerbs.VolumePath(path, unc, Letter(settings.DriveLetter) ?? (path.Length >= 2 && path[1] == ':' ? path[..2] : null), FolderLetters(unc))
            : @"\" + path.Trim('\\');
        if (volumePath is null)
        {
            say($"Not on the drive: {path}");
            return 1;
        }
        var terms = SearchTerms.Clean(string.Join(' ', words));
        var me = await api.GetMyHomeAsync(default);
        var tenantRoot = me.TenantRoot;
        var (engine, driveApi) = await AgentCommands.ToolEngineAsync(settings, api, client);
        if (await engine.GetEntryAsync(volumePath, default) is not { IsDirectory: true } entry)
        {
            say($"Not a folder on the drive: {volumePath}");
            return 1;
        }
        var scope = await SearchActions.ScopeAsync(entry, engine, driveApi.GetLinksAsync, me.OneDrive?.DriveId ?? "", tenantRoot, default);
        say($"Folder:     {volumePath}");
        say($"Area:       {scope.Area}");
        say($"Site:       {scope.SiteUrl ?? "-"}");
        say($"Scope URL:  {scope.ScopeUrl ?? "-"}");
        say($"Library:    {scope.LibraryUrl ?? "-"}");
        say($"Words:      [{terms}]");
        if (scope.ScopeUrl is { } scoped)
        {
            say($"KQL:        {SearchUrls.Kql(terms, scoped) ?? "(scope can't be quoted)"}");
        }
        var candidates = SearchUrls.Candidates(scope, terms);
        foreach (var (id, url) in candidates)
        {
            say($"{id,-18}  {url}");
        }
        say($"Requests:   Graph {client.GraphRequests}, SharePoint {client.SharePointRequests}");
        if (open is not null)
        {
            if (candidates.FirstOrDefault(c => c.Id.StartsWith(open, StringComparison.OrdinalIgnoreCase)) is not { Url: { } chosen })
            {
                say($"No candidate {open}");
                return 1;
            }
            Process.Start(new ProcessStartInfo(chosen) { UseShellExecute = true })?.Dispose();
            say($"Opened:     {chosen}");
        }
        return 0;
    }

    /// <summary>Counts a mount log's new listings per second as it grows (`mount --trace --verbose` written to a file).</summary>
    sealed class LogTail(string path)
    {
        long position;
        Encoding? encoding;
        string partial = "", second = "";
        int listings, graph;
        DateTime lastLine = DateTime.MinValue;

        public void Pump(Action<string> say, bool final = false)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < position)
                {
                    position = 0;
                    partial = "";
                }
                if (encoding is null && stream.Length >= 2)
                {
                    var bom = new byte[2];
                    stream.ReadExactly(bom);
                    encoding = bom is [0xFF, 0xFE] ? Encoding.Unicode : Encoding.UTF8;
                    // Only what the mount logs from now on; a UTF-16 file stays on character boundaries
                    position = stream.Length - (encoding == Encoding.Unicode ? stream.Length % 2 : 0);
                }
                if (encoding is not null && stream.Length > position)
                {
                    var length = (int)Math.Min(stream.Length - position, 16 << 20);
                    length -= encoding == Encoding.Unicode ? length % 2 : 0;
                    var buffer = new byte[length];
                    stream.Position = position;
                    stream.ReadExactly(buffer);
                    position += length;
                    var text = partial + encoding.GetString(buffer);
                    var lines = text.Split('\n');
                    partial = lines[^1];
                    foreach (var line in lines[..^1])
                    {
                        Count(line.Trim(), say);
                    }
                }
            }
            catch (IOException)
            {
            }
            if ((final || DateTime.UtcNow - lastLine > TimeSpan.FromSeconds(1.5)) && listings + graph > 0)
            {
                Flush(say);
            }
        }

        void Count(string line, Action<string> say)
        {
            if (line.Length < 13 || line[2] != ':' || line[5] != ':')
            {
                return;
            }
            lastLine = DateTime.UtcNow;
            var at = line[..8];
            if (at != second)
            {
                if (listings + graph > 0)
                {
                    Flush(say);
                }
                second = at;
            }
            if (line.Contains(" fs readdir ", StringComparison.Ordinal) && line.Contains(" marker=-", StringComparison.Ordinal))
            {
                listings++;
            }
            if (line.Contains(" GET ", StringComparison.Ordinal) && line.Contains("/children", StringComparison.Ordinal))
            {
                graph++;
            }
        }

        void Flush(Action<string> say)
        {
            say($"crawl {second}: {listings} folder listings, {graph} Graph listings");
            listings = graph = 0;
        }
    }

    /// <summary>Prints Shell Windows events.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ShellWindowsSink(Action<string> say) : DShellWindowsEvents
    {
        public void WindowRegistered(int cookie) => say($"event: WindowRegistered {cookie}");

        public void WindowRevoked(int cookie) => say($"event: WindowRevoked {cookie}");
    }

    /// <summary>Prints a tab's navigation events; with <paramref name="cancelIf"/>, cancels navigations it accepts.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class BrowserSink(long identity, Action<string> say, Func<string, bool>? cancelIf) : DWebBrowserEvents2
    {
        public void BeforeNavigate2(object browser, ref object url, ref object flags, ref object frame, ref object postData, ref object headers, ref bool cancel)
        {
            var target = url as string;
            say($"event: tab {identity:X} BeforeNavigate2 {target ?? url?.GetType().Name ?? "(null)"}");
            if (target is not null && cancelIf?.Invoke(target) == true)
            {
                cancel = true;
                say($"event: tab {identity:X} navigation CANCELLED");
            }
        }

        public void NavigateComplete2(object browser, ref object url) =>
            say($"event: tab {identity:X} NavigateComplete2 {url as string ?? url?.GetType().Name ?? "(null)"}");

        public void DocumentComplete(object browser, ref object url) =>
            say($"event: tab {identity:X} DocumentComplete {url as string ?? url?.GetType().Name ?? "(null)"}");
    }
}

/// <summary>A folder's background menu as Explorer's classic menu builds it, offscreen, for the spike.</summary>
internal static partial class ShellMenus
{
    public static string[] Background(string folder)
    {
        var pidl = Parse(folder);
        try
        {
            var folderId = typeof(IShellFolder).GUID;
            Marshal.ThrowExceptionForHR(SHBindToObject(IntPtr.Zero, pidl, IntPtr.Zero, ref folderId, out var bound));
            var shellFolder = (IShellFolder)Marshal.GetObjectForIUnknown(bound);
            Marshal.Release(bound);
            var menuId = typeof(IContextMenu).GUID;
            shellFolder.CreateViewObject(IntPtr.Zero, ref menuId, out var menu);
            return Texts((IContextMenu)menu);
        }
        finally
        {
            Marshal.FreeCoTaskMem(pidl);
        }
    }

    static IntPtr Parse(string path)
    {
        Marshal.ThrowExceptionForHR(SHParseDisplayName(path, IntPtr.Zero, out var pidl, 0, out _));
        return pidl;
    }

    static string[] Texts(IContextMenu menu)
    {
        var handle = CreatePopupMenu();
        try
        {
            _ = menu.QueryContextMenu(handle, 0, 1, 0x7FFF, 0x100); // CMF_EXTENDEDVERBS, as with Shift
            var texts = new List<string>();
            var buffer = new char[256];
            for (var i = 0; i < GetMenuItemCount(handle); i++)
            {
                var length = GetMenuStringW(handle, (uint)i, buffer, buffer.Length, 0x400); // MF_BYPOSITION
                if (length > 0)
                {
                    texts.Add(new string(buffer, 0, length).Replace("&", ""));
                }
            }
            return [.. texts];
        }
        finally
        {
            DestroyMenu(handle);
        }
    }

    [ComImport, Guid("000214E6-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellFolder
    {
        void ParseDisplayName(IntPtr window, IntPtr context, IntPtr name, IntPtr eaten, out IntPtr pidl, IntPtr attributes);
        void EnumObjects(IntPtr window, uint flags, out IntPtr list);
        void BindToObject(IntPtr pidl, IntPtr context, ref Guid riid, out IntPtr result);
        void BindToStorage(IntPtr pidl, IntPtr context, ref Guid riid, out IntPtr result);
        [PreserveSig]
        int CompareIDs(IntPtr lParam, IntPtr first, IntPtr second);
        void CreateViewObject(IntPtr owner, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object result);
        void GetAttributesOf(uint count, IntPtr pidls, ref uint attributes);
        void GetUIObjectOf(IntPtr owner, uint count, [In] IntPtr[] pidls, ref Guid riid, IntPtr reserved, [MarshalAs(UnmanagedType.Interface)] out object result);
    }

    [ComImport, Guid("000214E4-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr menu, uint index, uint first, uint last, uint flags);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHParseDisplayName(string name, IntPtr context, out IntPtr pidl, uint want, out uint attributes);

    [LibraryImport("shell32.dll")]
    private static partial int SHBindToObject(IntPtr folder, IntPtr pidl, IntPtr context, ref Guid riid, out IntPtr result);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreatePopupMenu();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(IntPtr menu);

    [LibraryImport("user32.dll")]
    private static partial int GetMenuItemCount(IntPtr menu);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetMenuStringW(IntPtr menu, uint item, [Out] char[] text, int capacity, uint flags);
}
