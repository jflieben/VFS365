using System.Collections.Concurrent;
using Vfs365.Core.Discovery;

namespace Vfs365.Core.Drive;

/// <summary>A library or OneDrive mounted in the namespace. The Graph drive ID is resolved on first use unless it is known.</summary>
internal sealed class DriveMount(Func<CancellationToken, Task<string>> resolveDriveId, bool readOnly, string? knownDriveId = null)
{
    readonly Lock gate = new();
    Task<string>? driveId = knownDriveId is null ? null : Task.FromResult(knownDriveId);

    public bool ReadOnly { get; } = readOnly;

    public Task<string> GetDriveIdAsync(CancellationToken ct)
    {
        lock (gate)
        {
            if (driveId is null || driveId.IsFaulted || driveId.IsCanceled)
            {
                driveId = resolveDriveId(ct);
            }
            return driveId;
        }
    }

    /// <summary>The drive ID if it is already known.</summary>
    public string? KnownDriveId => Volatile.Read(ref driveId) is { IsCompletedSuccessfully: true } known ? known.Result : null;
}

/// <summary>A folder of the fixed tree. Site is the web URL of a site's folder: what it shows, for change reports.</summary>
internal sealed class VirtualFolder(string name, DriveMount? drive = null, string? site = null)
{
    public string Name { get; } = name;
    public DriveMount? Drive { get; } = drive;
    public string? Site { get; } = site;
    public Dictionary<string, VirtualFolder> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    public VirtualFolder Add(VirtualFolder child)
    {
        Children.Add(child.Name, child);
        return child;
    }
}

/// <summary>Where a path lands: a virtual folder, or a path inside a mounted drive ("" for its root).</summary>
internal sealed record Resolved(string Path, VirtualFolder? Folder, DriveMount? Drive, string DrivePath);

/// <summary>What the volume shows. With only one of the two, it is the root of the volume.</summary>
public enum DriveScope { All, OneDrive, SharePoint }

/// <summary>
/// The fixed top of the tree: OneDrive\ and Sites\&lt;site&gt;\&lt;library&gt;\, or just one of them as the root.
/// The library set can be replaced while mounted (discovery finishing after the mount); unchanged libraries keep their mount.
/// Folder names depend only on the set of sites and libraries, never on the order discovery returns them: a title several
/// sites share gets each site's URL in brackets, so a remembered path never opens another site.
/// </summary>
public sealed class DriveNamespace
{
    readonly DriveScope scope;
    readonly Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId;
    readonly Action<LibraryEntry, string>? driveResolved;
    readonly DriveMount oneDriveMount;
    readonly Lock gate = new();
    Dictionary<string, DriveMount> mounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The tree with each mount's volume path, and the drive IDs found so far; replaced as a whole.</summary>
    sealed record Layout(VirtualFolder Root, (DriveMount Mount, string Path)[] Mounts)
    {
        public ConcurrentDictionary<string, string> ByDriveId { get; } = new(StringComparer.Ordinal);
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
    public static DriveNamespace Build(MyDrive oneDrive, IReadOnlyList<LibraryEntry> libraries, Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId,
        DriveScope scope = DriveScope.All, Action<LibraryEntry, string>? driveResolved = null)
    {
        var ns = new DriveNamespace(oneDrive, scope, resolveDriveId, driveResolved);
        ns.SetLibraries(libraries);
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
            var before = Snapshot(Root);
            var newMounts = new Dictionary<string, DriveMount>(StringComparer.OrdinalIgnoreCase);
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
                            : new DriveMount(ct => library.DriveId is { } id ? Task.FromResult(id) : ResolveAsync(library, ct), library.ReadOnly, library.DriveId);
                        newMounts[library.Key] = mount;
                        siteFolder.Add(new VirtualFolder(libraryNames[l], mount));
                        mounted.Add((mount, $"{sitesPath}\\{siteNames[s]}\\{libraryNames[l]}"));
                    }
                }
            }
            mounts = newMounts;
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
                        changed.Add($"{path}\\{name}"); // the same name, another site or library behind it
                    }
                }
            }
            return changed.Select(path => path.Length == 0 ? "\\" : path).ToList();
        }
    }

    async Task<string> ResolveAsync(LibraryEntry library, CancellationToken ct)
    {
        var driveId = await resolveDriveId(library, ct);
        driveResolved?.Invoke(library, driveId);
        return driveId;
    }

    /// <summary>Null when the path leaves the virtual tree at a name that doesn't exist.</summary>
    internal Resolved? Resolve(string path)
    {
        var segments = path.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        var node = Root;
        var canonical = "";
        for (var i = 0; i < segments.Length; i++)
        {
            if (node.Drive is not null)
            {
                return new Resolved(canonical + "\\" + string.Join('\\', segments[i..]), null, node.Drive, string.Join('/', segments[i..]));
            }
            if (!node.Children.TryGetValue(segments[i], out var child))
            {
                return null;
            }
            canonical += "\\" + child.Name;
            node = child;
        }
        return new Resolved(canonical.Length == 0 ? "\\" : canonical, node, node.Drive, "");
    }

    /// <summary>Volume path of the folder where a drive is mounted ("\" for the root); null when it isn't mounted or not resolved yet.</summary>
    public string? VolumePathOf(string driveId)
    {
        var current = Volatile.Read(ref layout);
        if (current.ByDriveId.TryGetValue(driveId, out var known))
        {
            return known;
        }
        string? found = null;
        foreach (var (mount, path) in current.Mounts)
        {
            if (mount.KnownDriveId is { } id)
            {
                current.ByDriveId.TryAdd(id, path);
                found ??= id == driveId ? path : null;
            }
        }
        return found;
    }

    /// <summary>Volume path of a library's folder by its key (siteId|webId|listId); null when it isn't shown.</summary>
    public string? VolumePathOfLibrary(string libraryKey)
    {
        lock (gate)
        {
            return mounts.GetValueOrDefault(libraryKey) is { } mount ? layout.Mounts.FirstOrDefault(m => m.Mount == mount).Path : null;
        }
    }

    static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", .. "0123456789\u00B9\u00B2\u00B3".SelectMany(n => new[] { $"COM{n}", $"LPT{n}" })],
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
    /// </summary>
    static string[] UniqueNames<T>(IReadOnlyList<T> siblings, Func<T, string> title, Func<T, string> order, Func<T, string[]> suffixes)
    {
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
                candidates = candidates.Select((name, i) => $"{name} ({options[i][level]})").ToList();
            }
            for (var i = 0; i < group.Count; i++)
            {
                var name = candidates[i];
                for (var n = 2; !taken.Add(name); n++)
                {
                    name = $"{candidates[i]} ({n})";
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
