using Vfs365.Core.Discovery;

namespace Vfs365.Core.Drive;

/// <summary>A library or OneDrive mounted in the namespace. The Graph drive ID is resolved on first use.</summary>
internal sealed class DriveMount(Func<CancellationToken, Task<string>> resolveDriveId, bool readOnly)
{
    readonly Lock gate = new();
    Task<string>? driveId;

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
    public string? KnownDriveId
    {
        get
        {
            lock (gate)
            {
                return driveId is { IsCompletedSuccessfully: true } known ? known.Result : null;
            }
        }
    }
}

internal sealed class VirtualFolder(string name, DriveMount? drive = null)
{
    public string Name { get; } = name;
    public DriveMount? Drive { get; } = drive;
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
/// </summary>
public sealed class DriveNamespace
{
    readonly MyDrive oneDrive;
    readonly DriveScope scope;
    readonly Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId;
    readonly Action<LibraryEntry, string>? driveResolved;
    readonly DriveMount oneDriveMount;
    readonly Lock gate = new();
    Dictionary<string, DriveMount> mounts = new(StringComparer.OrdinalIgnoreCase);
    VirtualFolder root;

    DriveNamespace(MyDrive oneDrive, DriveScope scope, Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId,
        Action<LibraryEntry, string>? driveResolved)
    {
        (this.oneDrive, this.scope, this.resolveDriveId, this.driveResolved) = (oneDrive, scope, resolveDriveId, driveResolved);
        oneDriveMount = new DriveMount(_ => Task.FromResult(oneDrive.DriveId), false);
        root = new VirtualFolder("");
    }

    internal VirtualFolder Root => Volatile.Read(ref root);

    /// <param name="driveResolved">Called when a library's drive ID was looked up, so it can be kept for the next start.</param>
    public static DriveNamespace Build(MyDrive oneDrive, IReadOnlyList<LibraryEntry> libraries, Func<LibraryEntry, CancellationToken, Task<string>> resolveDriveId,
        DriveScope scope = DriveScope.All, Action<LibraryEntry, string>? driveResolved = null)
    {
        var ns = new DriveNamespace(oneDrive, scope, resolveDriveId, driveResolved);
        ns.SetLibraries(libraries);
        return ns;
    }

    public MyDrive OneDrive => oneDrive;

    /// <summary>Replaces the libraries. Returns the volume paths of virtual folders whose children changed (for change notifications).</summary>
    public IReadOnlyList<string> SetLibraries(IReadOnlyList<LibraryEntry> libraries)
    {
        lock (gate)
        {
            var before = Snapshot(Root);
            var newMounts = new Dictionary<string, DriveMount>(StringComparer.OrdinalIgnoreCase);
            VirtualFolder newRoot;
            if (scope == DriveScope.OneDrive)
            {
                newRoot = new VirtualFolder("", oneDriveMount);
            }
            else
            {
                newRoot = new VirtualFolder("");
                var sites = newRoot;
                if (scope == DriveScope.All)
                {
                    newRoot.Add(new VirtualFolder("OneDrive", oneDriveMount));
                    sites = newRoot.Add(new VirtualFolder("Sites"));
                }
                foreach (var site in libraries.GroupBy(l => l.WebUrl, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.First().SiteTitle, StringComparer.OrdinalIgnoreCase))
                {
                    var siteFolder = sites.Add(new VirtualFolder(UniqueName(sites, SafeName(site.First().SiteTitle))));
                    foreach (var library in site.OrderBy(l => l.LibraryTitle, StringComparer.OrdinalIgnoreCase))
                    {
                        var mount = mounts.GetValueOrDefault(library.Key) is { ReadOnly: var wasReadOnly } existing && wasReadOnly == library.ReadOnly
                            ? existing
                            : new DriveMount(ct => library.DriveId is { } id ? Task.FromResult(id) : ResolveAsync(library, ct), library.ReadOnly);
                        newMounts[library.Key] = mount;
                        siteFolder.Add(new VirtualFolder(UniqueName(siteFolder, SafeName(library.LibraryTitle)), mount));
                    }
                }
            }
            mounts = newMounts;
            Volatile.Write(ref root, newRoot);
            var after = Snapshot(newRoot);
            return after.Keys.Union(before.Keys, StringComparer.OrdinalIgnoreCase)
                .Where(path => !before.TryGetValue(path, out var old) || !after.TryGetValue(path, out var now) || !old.SetEquals(now))
                .Select(path => path.Length == 0 ? "\\" : path)
                .ToList();
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
        static string? Find(VirtualFolder folder, string path, string driveId)
        {
            if (folder.Drive?.KnownDriveId == driveId)
            {
                return path.Length == 0 ? "\\" : path;
            }
            foreach (var child in folder.Children.Values)
            {
                if (Find(child, path + "\\" + child.Name, driveId) is { } found)
                {
                    return found;
                }
            }
            return null;
        }
        return Find(Root, "", driveId);
    }

    /// <summary>Windows-safe folder name for a site or library title.</summary>
    public static string SafeName(string title)
    {
        var chars = title.Select(c => c < ' ' || "\\/:*?\"<>|".Contains(c) ? '-' : c).ToArray();
        var name = new string(chars).Trim().TrimEnd('.');
        return name.Length == 0 ? "Unnamed" : name;
    }

    static string UniqueName(VirtualFolder parent, string name)
    {
        var candidate = name;
        for (var n = 2; parent.Children.ContainsKey(candidate); n++)
        {
            candidate = $"{name} ({n})";
        }
        return candidate;
    }

    /// <summary>Virtual folder path to its child names.</summary>
    static Dictionary<string, HashSet<string>> Snapshot(VirtualFolder top)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        void Walk(VirtualFolder folder, string path)
        {
            if (folder.Drive is not null)
            {
                return;
            }
            result[path] = new HashSet<string>(folder.Children.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var child in folder.Children.Values)
            {
                Walk(child, path + "\\" + child.Name);
            }
        }
        Walk(top, "");
        return result;
    }
}
