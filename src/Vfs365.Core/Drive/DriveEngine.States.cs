using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Vfs365.Core.Drive;

/// <summary>A file's state for Explorer's overlay icons. The values are the shell section's (vfs365shell.dll reads them).</summary>
public enum FileState { OnlineOnly = 0, Cached = 1, Uploading = 2, Downloading = 3, Problem = 4 }

/// <summary>The state of the file at a volume path ("\Sites\Finance\Documents\Offer.docx").</summary>
public readonly record struct VolumeFileState(string Path, FileState State);

/// <summary>
/// Per-file states for overlay icons, from what is in memory only (listings, local state, the content cache): no calls, no disk access.
/// Cached files are found per listing and kept until the listing or its drive's cached content changes, so a rebuild after a small
/// change doesn't look at every listed file again.
/// </summary>
public sealed partial class DriveEngine
{
    /// <summary>How long a file whose delete Microsoft 365 refused shows a problem.</summary>
    static readonly TimeSpan RefusedDeleteShown = TimeSpan.FromMinutes(10);

    long statesStamp;
    Action? statesChanged;
    readonly Lock statesGate = new();
    readonly ConcurrentDictionary<string, DateTimeOffset> refusedDeletes = new(StringComparer.OrdinalIgnoreCase);
    readonly ConditionalWeakTable<Listing, CachedFiles> cachedFiles = new();

    /// <summary>A listing's cached files at its version and its drive's cache version, at the volume paths of then.</summary>
    sealed record CachedFiles(int Version, long CacheVersion, string[] Folders, VolumeFileState[] Files);

    /// <summary>
    /// Raised when file states may have changed: uploads, deletes, renames, listings, the content cache, downloads. Raised on file system,
    /// transfer and cache threads, sometimes with locks held: handlers only note it and return.
    /// </summary>
    public event Action? StatesChanged
    {
        add
        {
            lock (statesGate)
            {
                if (statesChanged is null)
                {
                    content.Changed += MarkStates;
                }
                statesChanged += value;
            }
        }
        remove
        {
            lock (statesGate)
            {
                statesChanged -= value;
                if (statesChanged is null)
                {
                    content.Changed -= MarkStates;
                }
            }
        }
    }

    void MarkStates()
    {
        Interlocked.Increment(ref statesStamp);
        Volatile.Read(ref statesChanged)?.Invoke();
    }

    /// <summary>A notice about a file: a refused delete shows as a problem for a while.</summary>
    void NoteStates(NoticeKind kind, string driveId, string drivePath)
    {
        if (kind == NoticeKind.NotDeleted)
        {
            refusedDeletes[Key(driveId, drivePath)] = Now + RefusedDeleteShown;
        }
        MarkStates();
    }

    /// <summary>Something else is at the path now (saved, deleted): a refused delete there no longer shows.</summary>
    void ClearStates(string driveId, string drivePath)
    {
        if (!refusedDeletes.IsEmpty)
        {
            refusedDeletes.TryRemove(Key(driveId, drivePath), out _);
        }
    }

    /// <summary>
    /// Changes when <see cref="FileStates"/> may give something else. Cheap: one pass over listings (not their items) and local state.
    /// </summary>
    public long StatesVersion()
    {
        var sum = Mix(Volatile.Read(ref statesStamp), content.Version);
        foreach (var (_, listing) in listings)
        {
            sum += Mix(listing.Serial, listing.Version);
        }
        foreach (var (key, local) in overlay)
        {
            var bits = local switch
            {
                PendingFile pending => 1 | (pending.Dirty ? 4 : 0) | (Failing(pending) ? 8 : 0) | (pending.Rejected ? 16 : 0) | (pending.IsLive ? 32 : 0),
                AliasItem => 2,
                _ => 3,
            };
            sum += Mix(StringComparer.OrdinalIgnoreCase.GetHashCode(key), bits);
        }
        foreach (var (key, until) in refusedDeletes)
        {
            sum += until > Now ? Mix(StringComparer.OrdinalIgnoreCase.GetHashCode(key), 64) : 0;
        }
        return sum;
    }

    /// <summary>Uploads that failed this often in a row show Problem; fewer (a short outage, offline) still show Uploading.</summary>
    const int ProblemAfterFailures = 3;

    static bool Failing(PendingFile pending) => pending.Waiting && pending.Failures >= ProblemAfterFailures;

    /// <summary>
    /// Files with a state other than online only, by volume path: Problem (upload refused or failing, delete refused lately), Uploading
    /// (changed here and not uploaded yet), Downloading (a reader waits for its content), Cached (this version's content is here, or
    /// it is held here and never uploads). Local state wins over the listing at a path; a file hidden by a pending delete has none.
    /// </summary>
    public IReadOnlyList<VolumeFileState> FileStates()
    {
        lock (statesGate)
        {
            var states = new Dictionary<string, FileState>(StringComparer.OrdinalIgnoreCase);
            var downloads = content.Downloads();
            foreach (var (key, listing) in listings)
            {
                var split = key.IndexOf('|');
                var driveId = key[..split];
                var folders = FolderVolumePaths(driveId, key[(split + 1)..]);
                if (folders.Count == 0)
                {
                    continue;
                }
                // Read before the scan: a change during it scans again next time
                var version = listing.Version;
                var cacheVersion = content.VersionOf(driveId);
                if (!cachedFiles.TryGetValue(listing, out var known) || known.Version != version || known.CacheVersion != cacheVersion
                    || !known.Folders.SequenceEqual(folders, StringComparer.Ordinal))
                {
                    known = new CachedFiles(version, cacheVersion, [.. folders], CachedIn(driveId, folders, listing));
                    cachedFiles.AddOrUpdate(listing, known);
                }
                foreach (var file in known.Files)
                {
                    states[file.Path] = file.State;
                }
                foreach (var (downloadDrive, itemId) in downloads)
                {
                    if (downloadDrive == driveId && listing.ById.TryGetValue(itemId, out var name) && listing.Items.TryGetValue(name, out var item) && !item.IsFolder)
                    {
                        foreach (var folder in folders)
                        {
                            states[Join(folder, name)] = FileState.Downloading;
                        }
                    }
                }
            }
            foreach (var (key, until) in refusedDeletes)
            {
                if (until <= Now)
                {
                    refusedDeletes.TryRemove(new KeyValuePair<string, DateTimeOffset>(key, until));
                    continue;
                }
                var split = key.IndexOf('|');
                foreach (var path in VolumePathsOf(key[..split], key[(split + 1)..]))
                {
                    states[path] = FileState.Problem;
                }
            }
            foreach (var (_, local) in overlay)
            {
                var state = local switch
                {
                    PendingFile { IsLive: false } => (FileState?)null, // uploaded or discarded: the listing has what is there now
                    PendingFile pending when pending.Rejected || Failing(pending) => FileState.Problem,
                    PendingFile pending when pending.Dirty && !StaysLocal(pending) => FileState.Uploading,
                    PendingFile => FileState.Cached, // staged here unchanged, or a temp file that never uploads
                    AliasItem alias => content.IsCached(alias.DriveId, alias.Item.Id, alias.Item.CTag ?? alias.Item.ETag) ? FileState.Cached : FileState.OnlineOnly,
                    _ => FileState.OnlineOnly, // hidden: deleted or renamed away
                };
                if (state is not { } shown)
                {
                    continue;
                }
                foreach (var path in VolumePathsOf(local.DriveId, local.DrivePath))
                {
                    if (shown == FileState.OnlineOnly)
                    {
                        states.Remove(path);
                    }
                    else
                    {
                        states[path] = shown;
                    }
                }
            }
            return [.. states.Select(s => new VolumeFileState(s.Key, s.Value))];
        }
    }

    VolumeFileState[] CachedIn(string driveId, IReadOnlyList<string> folders, Listing listing)
    {
        List<VolumeFileState>? found = null;
        foreach (var (name, item) in listing.Items)
        {
            if (!item.IsFolder && content.IsCached(driveId, item.Id, item.CTag ?? item.ETag))
            {
                found ??= [];
                foreach (var folder in folders)
                {
                    found.Add(new VolumeFileState(Join(folder, name), FileState.Cached));
                }
            }
        }
        return found is null ? [] : [.. found];
    }

    /// <summary>The volume paths a folder of a drive shows at ("" for the drive's top): a library, and shared folders of that drive.</summary>
    IReadOnlyList<string> FolderVolumePaths(string driveId, string folderPath) => ns.VolumePathsOf(driveId, folderPath);
}
