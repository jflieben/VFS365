using Vfs365.Core.Discovery;

namespace Vfs365.Core.Drive;

/// <summary>
/// A library, OneDrive or shared item mounted in the namespace. The Graph drive ID is resolved on first use unless it is known.
/// BasePath: where the mount's top is in the drive ('/'-separated, "" for the drive's root; a folder shared with the user sits deeper
/// in its owner's drive). IsFile: the mount is one file (a file shared with the user), always read-only.
/// </summary>
internal sealed class DriveMount(Func<CancellationToken, Task<string>> resolveDriveId, bool readOnly, string? knownDriveId = null, string basePath = "",
    bool isFile = false, Action? resolved = null)
{
    readonly Lock gate = new();
    Task<string>? driveId = knownDriveId is null ? null : Task.FromResult(knownDriveId);

    public bool ReadOnly { get; } = readOnly || isFile;
    public string BasePath { get; } = basePath;
    public bool IsFile { get; } = isFile;

    public Task<string> GetDriveIdAsync(CancellationToken ct)
    {
        lock (gate)
        {
            if (driveId is null || driveId.IsFaulted || driveId.IsCanceled)
            {
                driveId = resolveDriveId(ct);
                if (resolved is not null)
                {
                    // After the task completes, so the namespace sees it as known when it is told
                    driveId.ContinueWith(t => resolved(), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                }
            }
            return driveId;
        }
    }

    /// <summary>The drive ID if it is already known.</summary>
    public string? KnownDriveId => Volatile.Read(ref driveId) is { IsCompletedSuccessfully: true } known ? known.Result : null;

    /// <summary>The drive path of a path relative to the mount's top ('/'-separated, "" for the top).</summary>
    public string DrivePathOf(string relative) => BasePath.Length == 0 ? relative : relative.Length == 0 ? BasePath : $"{BasePath}/{relative}";

    /// <summary>The path relative to the mount's top of a drive path it shows ("" for the top); null when the mount doesn't show it.</summary>
    public string? RelativeOf(string drivePath) =>
        BasePath.Length == 0 ? drivePath
        : drivePath.Equals(BasePath, StringComparison.OrdinalIgnoreCase) ? ""
        : !IsFile && drivePath.Length > BasePath.Length + 1 && drivePath[BasePath.Length] == '/' && drivePath.StartsWith(BasePath, StringComparison.OrdinalIgnoreCase)
            ? drivePath[(BasePath.Length + 1)..]
            : null;
}

/// <summary>
/// A folder of the fixed tree. Site is the web URL of a site's folder: what it shows, for change reports. Shared: the shared item a
/// child of the Shared with me folder shows (a folder, or with an <see cref="DriveMount.IsFile"/> mount a file). SharedRoot: the Shared
/// with me folder itself.
/// </summary>
internal sealed class VirtualFolder(string name, DriveMount? drive = null, string? site = null, SharedItem? shared = null, bool sharedRoot = false)
{
    public string Name { get; } = name;
    public DriveMount? Drive { get; } = drive;
    public string? Site { get; } = site;
    public SharedItem? Shared { get; } = shared;
    public bool SharedRoot { get; } = sharedRoot;
    public Dictionary<string, VirtualFolder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VirtualFolder Add(VirtualFolder child)
    {
        Children.Add(child.Name, child);
        return child;
    }
}

/// <summary>
/// Where a path lands: a virtual folder, or a path inside a mounted drive. DrivePath is the full path in the drive (the mount's base
/// path included). IsTop: the mount's top itself (a library's folder, a shared folder or file), which is part of the fixed tree.
/// </summary>
internal sealed record Resolved(string Path, VirtualFolder? Folder, DriveMount? Drive, string DrivePath, bool IsTop = false);

/// <summary>What the volume shows. With only one of the two, it is the root of the volume.</summary>
public enum DriveScope { All, OneDrive, SharePoint }

/// <summary>
/// The fixed top of the tree: OneDrive\, Sites\&lt;site&gt;\&lt;library&gt;\ and Shared with me\, or just the OneDrive or the sites as the
/// root. The library and shared sets can be replaced while mounted (discovery finishing after the mount); unchanged ones keep their mount.
/// Folder names depend only on the set of sites, libraries and shared items, never on the order they came in: a title several sites
/// share gets each site's URL in brackets, a name several shared items share gets each owner's name, so a remembered path never opens
/// another site or another person's item. A drive can be mounted at several places (a shared folder of a library also shown under Sites,
/// several folders shared from one person's OneDrive): <see cref="VolumePathsOf"/> maps a drive path to all of them.
/// </summary>
public sealed class DriveNamespace
{
    public const string SharedFolderName = "Shared with me";

    readonly DriveScope scope;
    readonly Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId;
    readonly Action<LibraryEntry, string>? driveResolved;
    readonly DriveMount oneDriveMount;
    readonly Lock gate = new();
    Dictionary<string, DriveMount> mounts = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<string, DriveMount> sharedMounts = new(StringComparer.Ordinal);
    IReadOnlyList<LibraryEntry> libraries = [];
    IReadOnlyList<SharedItem>? shared;
    long resolvedCount;

    /// <summary>The tree with each mount's volume path, replaced as a whole; ByDrive indexes the mounts whose drive ID is known.</summary>
    sealed record Layout(VirtualFolder Root, (DriveMount Mount, string Path)[] Mounts)
    {
        public (long Resolved, Dictionary<string, (DriveMount Mount, string Path)[]> Mounts)? ByDrive { get; set; }
    }

    Layout layout;

    DriveNamespace(MyDrive oneDrive, DriveScope scope, Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId,
        Action<LibraryEntry, string>? driveResolved)
    {
        (this.scope, this.resolveDriveId, this.driveResolved) = (scope, resolveDriveId, driveResolved);
        oneDriveMount = new DriveMount(_ => Task.FromResult(oneDrive.DriveId), false, oneDrive.DriveId);
        layout = new Layout(new VirtualFolder(""), []);
    }

    internal VirtualFolder Root => Volatile.Read(ref layout).Root;

    /// <param name="driveResolved">Called when a library's drive ID was looked up, so it can be kept for the next start.</param>
    /// <param name="shared">What others shared with the user, for the Shared with me folder (Scope All only); null: no such folder.</param>
    public static DriveNamespace Build(MyDrive oneDrive, IReadOnlyList<LibraryEntry> libraries, Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId,
        DriveScope scope = DriveScope.All, Action<LibraryEntry, string>? driveResolved = null, IReadOnlyList<SharedItem>? shared = null)
    {
        var ns = new DriveNamespace(oneDrive, scope, resolveDriveId, driveResolved);
        lock (ns.gate)
        {
            ns.shared = shared;
            ns.libraries = libraries;
            ns.Rebuild();
        }
        return ns;
    }

    /// <summary>
    /// Replaces the libraries. Returns the volume paths of virtual folders whose children changed, by name or by what they show (a
    /// name now another site's or library's), and of folders that show another site or library under their old name.
    /// </summary>
    public IReadOnlyList<string> SetLibraries(IReadOnlyList<LibraryEntry> libraries)
    {
        lock (gate)
        {
            this.libraries = libraries;
            return Rebuild();
        }
    }

    /// <summary>Replaces the shared items (null: no Shared with me folder); returns what changed as <see cref="SetLibraries"/> does.</summary>
    public IReadOnlyList<string> SetShared(IReadOnlyList<SharedItem>? items)
    {
        lock (gate)
        {
            shared = items;
            return Rebuild();
        }
    }

    /// <summary>Whether the volume has a Shared with me folder (Scope All with the list on).</summary>
    public bool ShowsShared => scope == DriveScope.All && Volatile.Read(ref shared) is not null;

    IReadOnlyList<string> Rebuild()
    {
        var before = Snapshot(Root);
        var newMounts = new Dictionary<string, DriveMount>(StringComparer.OrdinalIgnoreCase);
        var newShared = new Dictionary<string, DriveMount>(StringComparer.Ordinal);
        var mounted = new List<(DriveMount, string)>();
        VirtualFolder newRoot;
        if (scope == DriveScope.OneDrive)
        {
            newRoot = new VirtualFolder("", oneDriveMount);
            mounted.Add((oneDriveMount, "\\"));
        }
        else
        {
            newRoot = new VirtualFolder("");
            var sites = newRoot;
            var sitesPath = "";
            if (scope == DriveScope.All)
            {
                newRoot.Add(new VirtualFolder("OneDrive", oneDriveMount));
                mounted.Add((oneDriveMount, "\\OneDrive"));
                sites = newRoot.Add(new VirtualFolder("Sites"));
                sitesPath = "\\Sites";
            }
            // A web's title and URL come from its first library by key, so they don't depend on the order of the list either
            var webs = libraries.DistinctBy(l => l.Key).GroupBy(l => l.WebUrl, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(l => l.Key, StringComparer.Ordinal).ToList())
                .ToList();
            var siteNames = UniqueNames(webs, w => SafeName(w[0].SiteTitle), w => w[0].WebUrl, w => SiteSuffixes(w[0].WebUrl));
            for (var s = 0; s < webs.Count; s++)
            {
                var web = webs[s];
                var siteFolder = sites.Add(new VirtualFolder(siteNames[s], site: web[0].WebUrl.ToUpperInvariant()));
                var libraryNames = UniqueNames(web, l => SafeName(l.LibraryTitle), l => l.Key, LibrarySuffixes);
                for (var l = 0; l < web.Count; l++)
                {
                    var library = web[l];
                    var mount = mounts.GetValueOrDefault(library.Key) is { ReadOnly: var wasReadOnly } existing && wasReadOnly == library.ReadOnly
                        ? existing
                        : new DriveMount(ct => library.DriveId is { } id ? Task.FromResult(id) : ResolveAsync(library, ct), library.ReadOnly, library.DriveId,
                            resolved: Resolved);
                    newMounts[library.Key] = mount;
                    siteFolder.Add(new VirtualFolder(libraryNames[l], mount));
                    mounted.Add((mount, $"{sitesPath}\\{siteNames[s]}\\{libraryNames[l]}"));
                }
            }
            if (scope == DriveScope.All && shared is not null)
            {
                var sharedFolder = newRoot.Add(new VirtualFolder(SharedFolderName, sharedRoot: true));
                var items = Mountable(shared);
                var names = UniqueSharedNames(items);
                for (var i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var mount = sharedMounts.GetValueOrDefault(item.Key) is { } existing && existing.BasePath == item.Path && existing.IsFile == !item.IsFolder
                        && existing.ReadOnly == (item.ReadOnly || !item.IsFolder)
                        ? existing
                        : new DriveMount(_ => Task.FromResult(item.DriveId), item.ReadOnly, item.DriveId, item.Path!, isFile: !item.IsFolder);
                    newShared[item.Key] = mount;
                    sharedFolder.Add(new VirtualFolder(names[i], mount, shared: item));
                    mounted.Add((mount, $"\\{SharedFolderName}\\{names[i]}"));
                }
            }
        }
        (mounts, sharedMounts) = (newMounts, newShared);
        Volatile.Write(ref layout, new Layout(newRoot, [.. mounted]));
        var after = Snapshot(newRoot);
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in after.Keys.Union(before.Keys, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.TryGetValue(path, out var old) || !after.TryGetValue(path, out var now) || old.Count != now.Count)
            {
                changed.Add(path);
                continue;
            }
            foreach (var (name, shows) in now)
            {
                if (!old.TryGetValue(name, out var showed))
                {
                    changed.Add(path);
                }
                else if (!Equals(shows, showed))
                {
                    changed.Add(path);
                    changed.Add($"{path}\\{name}"); // the same name, another site, library or shared item behind it
                }
            }
        }
        return changed.Select(path => path.Length == 0 ? "\\" : path).ToList();
    }

    /// <summary>Shared items that can be shown: their place in the owner's drive is known; one per drive item.</summary>
    static List<SharedItem> Mountable(IReadOnlyList<SharedItem> items) =>
        items.Where(i => i.Path is { Length: > 0 }).DistinctBy(i => i.Key).OrderBy(i => i.Key, StringComparer.Ordinal).ToList();

    /// <summary>
    /// The names shared items get in the Shared with me folder, by key: the item's name, or when several share it, each with its owner
    /// in brackets (before a file's extension; the owner's address where two owners of such items have the same name), and numbers in
    /// key order for one owner's items that still clash.
    /// </summary>
    public static IReadOnlyDictionary<string, string> SharedNames(IReadOnlyList<SharedItem> items)
    {
        var mountable = Mountable(items);
        var names = UniqueSharedNames(mountable);
        return mountable.Select((item, i) => (item.Key, Name: names[i])).ToDictionary(n => n.Key, n => n.Name, StringComparer.Ordinal);
    }

    static string[] UniqueSharedNames(IReadOnlyList<SharedItem> items)
    {
        static string Owner(SharedItem item) => item.OwnerName ?? item.OwnerEmail ?? "unknown";
        var byAddress = items.GroupBy(i => SafeName(i.Name), StringComparer.OrdinalIgnoreCase)
            .SelectMany(same => same.GroupBy(Owner, StringComparer.OrdinalIgnoreCase)
                .Where(owners => owners.Select(i => i.OwnerEmail ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                .SelectMany(owners => owners))
            .Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        return UniqueNames(items, i => SafeName(i.Name), i => i.Key, i => [SafeName(byAddress.Contains(i.Key) ? i.OwnerEmail ?? Owner(i) : Owner(i))],
            (item, name, suffix) => item.IsFolder || Path.GetExtension(name) is not { Length: > 1 } extension
                ? $"{name} ({suffix})"
                : $"{name[..^extension.Length]} ({suffix}){extension}");
    }

    void Resolved() => Interlocked.Increment(ref resolvedCount);

    async Task<string> ResolveAsync(LibraryEntry library, CancellationToken ct)
    {
        var driveId = await resolveDriveId(library, ct);
        driveResolved?.Invoke(library, driveId);
        return driveId;
    }

    /// <summary>Null when the path leaves the virtual tree at a name that doesn't exist, or goes below a file.</summary>
    internal Resolved? Resolve(string path)
    {
        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var node = Root;
        var canonical = "";
        for (var i = 0; i < segments.Length; i++)
        {
            if (node.Drive is { } drive)
            {
                return drive.IsFile ? null : new Resolved(canonical + "\\" + string.Join('\\', segments[i..]), null, drive, drive.DrivePathOf(string.Join('/', segments[i..])));
            }
            if (!node.Children.TryGetValue(segments[i], out var child))
            {
                return null;
            }
            canonical += "\\" + child.Name;
            node = child;
        }
        return new Resolved(canonical.Length == 0 ? "\\" : canonical, node.Drive is { IsFile: true } ? null : node, node.Drive, node.Drive?.BasePath ?? "",
            IsTop: node.Drive is not null);
    }

    /// <summary>The mounts of each known drive ID with their volume paths, built again when a library's drive ID was looked up since.</summary>
    Dictionary<string, (DriveMount Mount, string Path)[]> ByDrive()
    {
        var current = Volatile.Read(ref layout);
        var resolved = Interlocked.Read(ref resolvedCount);
        if (current.ByDrive is { } known && known.Resolved == resolved)
        {
            return known.Mounts;
        }
        var index = current.Mounts.Where(m => m.Mount.KnownDriveId is not null).GroupBy(m => m.Mount.KnownDriveId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        current.ByDrive = (resolved, index);
        return index;
    }

    /// <summary>Volume path of the top of a drive's first mount ("\" for the root); null when it isn't mounted or not resolved yet.</summary>
    public string? VolumePathOf(string driveId) => ByDrive().TryGetValue(driveId, out var found) ? found[0].Path : null;

    /// <summary>
    /// Every volume path that shows a drive path ('/'-separated, "" for the drive's root): one per mount that covers it, a library or
    /// OneDrive first. Empty when no mount shows it (not mounted, not resolved yet, or outside the shared folders of that drive).
    /// </summary>
    public IReadOnlyList<string> VolumePathsOf(string driveId, string drivePath)
    {
        if (!ByDrive().TryGetValue(driveId, out var found))
        {
            return [];
        }
        var paths = new List<string>(found.Length);
        foreach (var (mount, top) in found)
        {
            if (mount.RelativeOf(drivePath) is { } relative)
            {
                paths.Add(Join(top, relative));
            }
        }
        return paths;
    }

    /// <summary>The first of <see cref="VolumePathsOf"/>, or the one through <paramref name="prefer"/> when that mount shows it.</summary>
    internal string? VolumePathOf(string driveId, string drivePath, DriveMount? prefer = null)
    {
        if (!ByDrive().TryGetValue(driveId, out var found))
        {
            return null;
        }
        string? first = null;
        foreach (var (mount, top) in found)
        {
            if (mount.RelativeOf(drivePath) is { } relative)
            {
                if (prefer is null || ReferenceEquals(mount, prefer))
                {
                    return Join(top, relative);
                }
                first ??= Join(top, relative);
            }
        }
        return first;
    }

    /// <summary>
    /// The drive is mounted, but only below its root and not at <paramref name="drivePath"/>: a folder above a folder or file shared
    /// with the user, which the user can't list.
    /// </summary>
    internal bool Hides(string driveId, string drivePath) =>
        ByDrive().TryGetValue(driveId, out var found) && found.All(m => m.Mount.RelativeOf(drivePath) is null);

    /// <summary>The drive is mounted only below its root (shared folders and files): it has no change feed, only listings read again.</summary>
    internal bool MountsOnlyBelowRoot(string driveId) => ByDrive().TryGetValue(driveId, out var found) && found.All(m => m.Mount.BasePath.Length > 0);

    /// <summary>The volume paths of the tops of a drive's mounts.</summary>
    public IReadOnlyList<string> MountTopsOf(string driveId) => ByDrive().TryGetValue(driveId, out var found) ? [.. found.Select(m => m.Path)] : [];

    static string Join(string top, string relative) =>
        relative.Length == 0 ? top : top == "\\" ? "\\" + relative.Replace('/', '\\') : $"{top}\\{relative.Replace('/', '\\')}";

    /// <summary>Volume path of a library's folder by its key (siteId|webId|listId); null when it isn't shown.</summary>
    public string? VolumePathOfLibrary(string libraryKey)
    {
        lock (gate)
        {
            return mounts.GetValueOrDefault(libraryKey) is { } mount ? layout.Mounts.FirstOrDefault(m => m.Mount == mount).Path : null;
        }
    }

    /// <summary>Volume path of a shared item by its key (driveId|itemId); null when it isn't shown.</summary>
    public string? VolumePathOfShared(string sharedKey)
    {
        lock (gate)
        {
            return sharedMounts.GetValueOrDefault(sharedKey) is { } mount ? layout.Mounts.FirstOrDefault(m => m.Mount == mount).Path : null;
        }
    }

    static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", .. "0123456789¹²³".SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Windows-safe folder name for a site or library title. Device names (CON, NUL, COM1 and so on, also before a dot) get an
    /// underscore: Windows opens the device instead of a folder with that name.
    /// </summary>
    public static string SafeName(string title)
    {
        var chars = title.Select(c => c < ' ' || "\\/:*?\"<>|".Contains(c) ? '-' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.');
        if (name.Length == 0)
        {
            return "Unnamed";
        }
        var stem = (name.IndexOf('.') is var dot and >= 0 ? name[..dot] : name).TrimEnd(' ');
        return DeviceNames.Contains(stem) ? stem + "_" + name[stem.Length..] : name;
    }

    /// <summary>
    /// A name per sibling: a title used once is the name. Siblings sharing a title (after cleanup) all get a suffix in brackets, the
    /// first of <paramref name="suffixes"/> that tells them apart, so none of them keeps the bare title for another to take later.
    /// Names that still clash (a title that looks like a suffixed one) get a number in <paramref name="order"/>'s order.
    /// <paramref name="decorate"/> puts a suffix or number in a name (default: at its end).
    /// </summary>
    static string[] UniqueNames<T>(IReadOnlyList<T> siblings, Func<T, string> title, Func<T, string> order, Func<T, string[]> suffixes,
        Func<T, string, string, string>? decorate = null)
    {
        decorate ??= (_, name, suffix) => $"{name} ({suffix})";
        var names = new string[siblings.Count];
        var groups = Enumerable.Range(0, siblings.Count)
            .GroupBy(i => title(siblings[i]), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(i => order(siblings[i]), StringComparer.Ordinal).ToList())
            .OrderBy(g => g.Count > 1) // titles used once claim their names first
            .ThenBy(g => title(siblings[g[0]]), StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => order(siblings[g[0]]), StringComparer.Ordinal)
            .ToList();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var candidates = group.Select(i => title(siblings[i])).ToList();
            if (group.Count > 1)
            {
                var options = group.Select(i => suffixes(siblings[i])).ToList();
                var levels = options.Min(o => o.Length);
                var level = Enumerable.Range(0, levels)
                    .FirstOrDefault(l => options.Select(o => o[l]).Distinct(StringComparer.OrdinalIgnoreCase).Count() == group.Count, levels - 1);
                candidates = candidates.Select((name, i) => decorate(siblings[group[i]], name, options[i][level])).ToList();
            }
            for (var i = 0; i < group.Count; i++)
            {
                var name = candidates[i];
                for (var n = 2; !taken.Add(name); n++)
                {
                    name = decorate(siblings[group[i]], candidates[i], n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                names[group[i]] = name;
            }
        }
        return names;
    }

    /// <summary>What tells same-titled sites apart, shortest first: the URL's last segment ("projects-us"), its path, host and path.</summary>
    static string[] SiteSuffixes(string webUrl)
    {
        if (!Uri.TryCreate(webUrl, UriKind.Absolute, out var uri))
        {
            return [SafeName(webUrl)];
        }
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var host = uri.Host.Split('.')[0];
        var path = segments.Length == 0 ? host : string.Join('-', segments);
        return [SafeName(segments.Length == 0 ? host : segments[^1]), SafeName(path), SafeName(segments.Length == 0 ? uri.Host : $"{uri.Host}-{path}")];
    }

    /// <summary>What tells same-titled libraries of a site apart: the internal name, else the start of the list ID, then the list ID.</summary>
    static string[] LibrarySuffixes(LibraryEntry library)
    {
        var id = library.ListId.Trim('{', '}');
        var shortId = id.Replace("-", "") is { Length: > 8 } compact ? compact[..8] : id;
        return [SafeName(library.InternalName ?? shortId), SafeName(shortId), SafeName(id), SafeName(library.Key)];
    }

    /// <summary>Virtual folder path to its children's names, each with what it shows: a mount, a site's URL or nothing.</summary>
    static Dictionary<string, Dictionary<string, object?>> Snapshot(VirtualFolder top)
    {
        var result = new Dictionary<string, Dictionary<string, object?>>(StringComparer.OrdinalIgnoreCase);
        void Walk(VirtualFolder folder, string path)
        {
            if (folder.Drive is not null)
            {
                return;
            }
            result[path] = folder.Children.Values.ToDictionary(c => c.Name, c => (object?)c.Drive ?? c.Site, StringComparer.OrdinalIgnoreCase);
            foreach (var child in folder.Children.Values)
            {
                Walk(child, path + "\\" + child.Name);
            }
        }
        Walk(top, "");
        return result;
    }
}
