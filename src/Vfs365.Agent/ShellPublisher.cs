using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

/// <summary>
/// The states readers get, by key hash: the engine's files, and folders above a problem or an upload (the highest of those below them,
/// up to the volume's top folders). At most <see cref="ShellImage.MaxEntries"/>: cached files go first, then downloads.
/// </summary>
public sealed class ShellTable
{
    public static readonly ShellTable Empty = new([], []);

    ShellTable(Dictionary<ulong, (string Path, FileState State)> entries, Dictionary<string, ulong> hashes) => (Entries, this.hashes) = (entries, hashes);

    readonly Dictionary<string, ulong> hashes;

    public IReadOnlyDictionary<ulong, (string Path, FileState State)> Entries { get; }

    /// <summary>For the image: highest ranked first.</summary>
    public IEnumerable<(ulong Hash, FileState State)> Slots => Entries.Select(e => (e.Key, e.Value.State));

    /// <summary>Problem over Uploading over Downloading over Cached.</summary>
    public static int Rank(FileState state) => state switch
    {
        FileState.Problem => 4,
        FileState.Uploading => 3,
        FileState.Downloading => 2,
        FileState.Cached => 1,
        _ => 0,
    };

    /// <param name="previous">Its paths' hashes are used again, so a rebuild uppercases only new paths.</param>
    public static ShellTable From(IEnumerable<VolumeFileState> files, ShellTable? previous = null)
    {
        var known = previous?.hashes;
        var hashes = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var entries = new Dictionary<ulong, (string Path, FileState State)>();
        ulong HashOf(string path)
        {
            if (!hashes.TryGetValue(path, out var hash))
            {
                hashes[path] = hash = known is not null && known.TryGetValue(path, out var earlier) ? earlier : ShellImage.KeyHash(path);
            }
            return hash;
        }
        bool Put(string path, FileState state)
        {
            var hash = HashOf(path);
            if (entries.TryGetValue(hash, out var held) && Rank(held.State) >= Rank(state))
            {
                return false;
            }
            entries[hash] = (path, state);
            return true;
        }
        foreach (var (path, state) in files)
        {
            if (state == FileState.OnlineOnly || !Put(path, state) || state is not (FileState.Problem or FileState.Uploading))
            {
                continue;
            }
            // Folders above show it too; one that shows as much already has its folders done
            for (var folder = Parent(path); folder != @"\" && Put(folder, state); folder = Parent(folder))
            {
            }
        }
        if (entries.Count > ShellImage.MaxEntries)
        {
            var kept = entries.OrderByDescending(e => Rank(e.Value.State)).ThenBy(e => e.Value.Path, StringComparer.Ordinal).Take(ShellImage.MaxEntries).ToList();
            entries = kept.ToDictionary(e => e.Key, e => e.Value);
        }
        return new ShellTable(entries, hashes);
    }

    /// <summary>The volume path's folder ("\a\b" to "\a", "\a" to "\").</summary>
    public static string Parent(string path) => path.LastIndexOf('\\') is var split and > 0 ? path[..split] : @"\";

    /// <summary>Paths whose state differs between two tables (came, went or changed).</summary>
    public static IReadOnlyList<string> Changed(ShellTable before, ShellTable after)
    {
        var changed = new List<string>();
        foreach (var (hash, (path, state)) in after.Entries)
        {
            if (!before.Entries.TryGetValue(hash, out var old) || old.State != state)
            {
                changed.Add(path);
            }
        }
        foreach (var (hash, (path, _)) in before.Entries)
        {
            if (!after.Entries.ContainsKey(hash))
            {
                changed.Add(path);
            }
        }
        return changed;
    }
}

/// <summary>What Explorer is told after a rebuild: SHChangeNotify per changed path in every place it shows, or folders when too many changed.</summary>
public static class ShellNotices
{
    public const int UpdateItem = 0x2000, UpdateDir = 0x1000; // SHCNE_UPDATEITEM, SHCNE_UPDATEDIR

    /// <summary>
    /// Each changed volume path in every root form that holds it, as UpdateItem; more than <paramref name="cap"/> of those: their folders
    /// as UpdateDir; still too many: the roots.
    /// </summary>
    public static IReadOnlyList<(int Event, string Path)> Plan(IReadOnlyCollection<string> changed, IReadOnlyList<ShellRoot> roots, int cap = 500)
    {
        if (changed.Count == 0 || roots.Count == 0)
        {
            return [];
        }
        var items = changed.SelectMany(path => Forms(path, roots)).Distinct(StringComparer.OrdinalIgnoreCase).Take(cap + 1).ToList();
        if (items.Count <= cap)
        {
            return [.. items.Select(path => (UpdateItem, path))];
        }
        var folders = changed.Select(ShellTable.Parent).Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(folder => Forms(folder, roots)).Distinct(StringComparer.OrdinalIgnoreCase).Take(cap + 1).ToList();
        return folders.Count <= cap ? [.. folders.Select(path => (UpdateDir, path))] : [.. roots.Select(root => (UpdateDir, root.Prefix))];
    }

    /// <summary>Where Explorer shows a volume path: "\Sites\A\b.txt" as "M:\Sites\A\b.txt", "\\VFS365\ann\Sites\A\b.txt" and "Z:\b.txt".</summary>
    public static IEnumerable<string> Forms(string volumePath, IReadOnlyList<ShellRoot> roots)
    {
        var below = volumePath == @"\" ? @"\" : volumePath + @"\";
        foreach (var (prefix, target) in roots)
        {
            if (below.StartsWith(target, StringComparison.OrdinalIgnoreCase))
            {
                var rest = below[target.Length..];
                yield return rest.Length == 0 ? prefix : prefix + rest.TrimEnd('\\');
            }
        }
    }
}

/// <summary>
/// Keeps the shell section current for the mounted drive: roots and strings from the Explorer menu, file states from the engine. Rebuilds
/// on the engine's signal after a short wait (more changes join), and every few seconds when something changed unsignalled; never more
/// than a small share of the time. Disposed (a clean stop), it clears the section so the menu and the overlays go.
/// </summary>
public sealed class ShellPublisher : IDisposable
{
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500), SafetyInterval = TimeSpan.FromSeconds(2);

    readonly DriveEngine engine;
    readonly ShellSection section;
    readonly bool overlays;
    readonly IReadOnlyList<(string Key, string Value)> strings;
    readonly Action<string> log;
    readonly Action<int, string> notify;
    readonly byte[] image = new byte[ShellImage.SectionSize];
    readonly Lock gate = new(), rootsGate = new();
    readonly SemaphoreSlim wake = new(0);
    readonly CancellationTokenSource stop = new();
    readonly Task running;
    IReadOnlyList<ShellRoot> roots = [];
    IReadOnlyList<ShellRoot>? publishedRoots;
    ShellTable current = ShellTable.Empty;
    long publishedVersion;
    int signaled;
    string? lastProblem;
    bool closed;

    /// <param name="notify">SHChangeNotify by default; tests catch the calls.</param>
    public ShellPublisher(DriveEngine engine, ShellSection section, bool overlays, IReadOnlyList<(string Key, string Value)> strings, Action<string> log,
        Action<int, string>? notify = null, bool background = true)
    {
        (this.engine, this.section, this.overlays, this.strings, this.log) = (engine, section, overlays, strings, log);
        this.notify = notify ?? Notify;
        if (overlays)
        {
            engine.StatesChanged += Wake;
        }
        running = background ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    /// <summary>The menu's texts in <paramref name="culture"/>: "Verb.Menu" and "Verb.&lt;name&gt;" for every verb.</summary>
    public static IReadOnlyList<(string Key, string Value)> MenuStrings(CultureInfo culture) =>
        [("Verb.Menu", UiText.Get(culture, "Verb.Menu")), .. ExplorerVerbs.Verbs.Select(v => ($"Verb.{v.Name}", UiText.Get(culture, $"Verb.{v.Name}")))];

    /// <summary>The places Explorer shows the volume now; published when they changed.</summary>
    public void SetRoots(IReadOnlyList<ShellRoot> now)
    {
        lock (rootsGate) // not the gate: a rebuild running never holds up the menu's timer
        {
            if (Volatile.Read(ref roots).SequenceEqual(now))
            {
                return;
            }
            Volatile.Write(ref roots, [.. now]);
        }
        Wake();
    }

    void Wake()
    {
        if (Interlocked.Exchange(ref signaled, 1) == 0)
        {
            wake.Release();
        }
    }

    async Task RunAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(SafetyInterval, stop.Token);
                await Task.Delay(Debounce, stop.Token);
                Volatile.Write(ref signaled, 0);
                var started = Stopwatch.GetTimestamp();
                Publish();
                // A big table keeps rebuilds a small share of the time
                if (Stopwatch.GetElapsedTime(started) is { TotalMilliseconds: > 50 } took)
                {
                    await Task.Delay(took * 4, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                if (e.Message != lastProblem)
                {
                    lastProblem = e.Message;
                    log($"Explorer: overlay icons not updated: {e.Message}");
                }
            }
        }
    }

    /// <summary>Rebuilds the section when the roots or the engine's states changed since the last time (or always, <paramref name="force"/>).</summary>
    public void Publish(bool force = false)
    {
        lock (gate)
        {
            if (closed)
            {
                return;
            }
            var roots = Volatile.Read(ref this.roots);
            var version = overlays ? engine.StatesVersion() : 0; // read first: a change while this runs publishes again
            if (!force && ReferenceEquals(roots, publishedRoots) && version == publishedVersion)
            {
                return;
            }
            var table = overlays ? ShellTable.From(engine.FileStates(), current) : ShellTable.Empty;
            var length = ShellImage.Build(image, (uint)Environment.ProcessId, ShellImage.MenuFlag | (overlays ? ShellImage.OverlaysFlag : 0), roots, strings, table.Slots);
            section.Write(image.AsSpan(0, length));
            var changed = ShellTable.Changed(current, table);
            (current, publishedRoots, publishedVersion) = (table, roots, version);
            Tell(changed, roots);
        }
    }

    void Tell(IReadOnlyCollection<string> changed, IReadOnlyList<ShellRoot> where)
    {
        foreach (var (what, path) in ShellNotices.Plan(changed, where))
        {
            notify(what, path);
        }
    }

    /// <summary>A clean stop: the section says no menu and no overlays, and Explorer draws again what had one.</summary>
    public void Dispose()
    {
        if (overlays)
        {
            engine.StatesChanged -= Wake;
        }
        stop.Cancel();
        running.Wait(TimeSpan.FromSeconds(2)); // never faults
        lock (gate)
        {
            if (closed)
            {
                return;
            }
            closed = true;
            try
            {
                section.Clear();
                var roots = Volatile.Read(ref this.roots);
                Tell([.. current.Entries.Values.Select(e => e.Path)], roots);
                foreach (var root in roots)
                {
                    notify(ShellNotices.UpdateDir, root.Prefix);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"Explorer: could not clear the overlay icons: {e.Message}");
            }
            finally
            {
                section.Dispose();
            }
        }
    }

    static void Notify(int what, string path) => SHChangeNotify(what, 0x0005 | 0x3000, path, null); // SHCNF_PATHW | SHCNF_FLUSHNOWAIT

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string path, string? other);
}
