using System.Collections.Concurrent;

namespace Vfs365.Core.Drive;

public sealed partial class DriveEngine
{
    /// <summary>
    /// Raised when a folder listing starts (Explorer, an app, a search crawl); loads ahead, refreshes and change feeds don't raise it.
    /// Raised on file system threads: handlers only note it and return, and never throw. The Explorer search watcher wakes on it.
    /// </summary>
    public event Action? FolderListed;

    /// <summary>What a page of a folder listing costs in SharePoint's resource units.</summary>
    public const int ListingPageUnits = 2;

    readonly ConcurrentDictionary<ListingMeter, byte> meters = new();

    /// <summary>
    /// Counts from now what folder listings read from Microsoft 365 at or below <paramref name="volumePath"/> cost, loads ahead and
    /// refreshes included (Explorer's own search crawl, policy ExplorerSearch Allow). Cached listings cost nothing. Dispose to stop.
    /// </summary>
    public ListingMeter MeasureListings(string volumePath)
    {
        var meter = new ListingMeter(volumePath.TrimEnd('\\'), this);
        meters[meter] = 0;
        return meter;
    }

    /// <summary>A listing page arrived from Microsoft 365: counted for the meters above it.</summary>
    void Charge(string driveId, string folderPath)
    {
        if (meters.IsEmpty || ns.VolumePathOf(driveId, folderPath) is not { } path)
        {
            return;
        }
        foreach (var meter in meters.Keys)
        {
            if (meter.Covers(path))
            {
                meter.Add(ListingPageUnits);
            }
        }
    }

    public sealed class ListingMeter : IDisposable
    {
        readonly string below;
        readonly DriveEngine engine;
        int units;

        internal ListingMeter(string below, DriveEngine engine) => (this.below, this.engine) = (below, engine);

        /// <summary>Resource units counted so far.</summary>
        public int Units => Volatile.Read(ref units);

        internal void Add(int cost) => Interlocked.Add(ref units, cost);

        internal bool Covers(string path) =>
            below.Length == 0
            || path.StartsWith(below, StringComparison.OrdinalIgnoreCase) && (path.Length == below.Length || path[below.Length] == '\\');

        public void Dispose() => engine.meters.TryRemove(this, out _);
    }
}
