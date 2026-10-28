using System.Text.Json;
using Vfs365.Core.Discovery;

namespace Vfs365.Agent;

/// <summary>Discovery state per user in %LOCALAPPDATA%\VFS365\discovery.json.</summary>
static class DiscoveryStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFS365");

    public static string FilePath { get; } = Path.Combine(DataDirectory, "discovery.json");

    /// <summary>Null when missing or unreadable: discovery then starts fresh. <paramref name="path"/>: a file elsewhere (support bundles, tests).</summary>
    public static DiscoveryState? Load(string? path = null)
    {
        path ??= FilePath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<DiscoveryState>(File.ReadAllText(path), Json) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static void Save(DiscoveryState state)
    {
        Directory.CreateDirectory(DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>
/// The mounted session's discovery state: replaced by background discovery, amended with drive IDs looked up on first use, saved when
/// changed (to discovery.json, or with <paramref name="save"/>). A save that fails stays due and is tried again at the next SaveIfChanged.
/// </summary>
public sealed class DiscoveryStateKeeper(DiscoveryState initial, Action<DiscoveryState>? save = null)
{
    readonly Lock gate = new();
    readonly Action<DiscoveryState> save = save ?? DiscoveryStore.Save;
    DiscoveryState current = initial;
    bool dirty;

    public DiscoveryState Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    /// <summary>A new discovery result; drive IDs learned meanwhile are kept, and so is the shared list (read on its own).</summary>
    public void Replace(DiscoveryState state)
    {
        lock (gate)
        {
            var known = current.Libraries.Where(l => l.DriveId is not null).ToDictionary(l => l.Key, l => l.DriveId!, StringComparer.OrdinalIgnoreCase);
            current = state with
            {
                Libraries = state.Libraries.Select(l => l.DriveId is null && known.TryGetValue(l.Key, out var id) ? l with { DriveId = id } : l).ToList(),
                Shared = current.Shared,
            };
            dirty = true;
        }
    }

    /// <summary>A new shared list (the Shared with me folder).</summary>
    public void SetShared(SharedState shared)
    {
        lock (gate)
        {
            current = current with { Shared = shared };
            dirty = true;
        }
    }

    public void RememberDriveId(string libraryKey, string driveId)
    {
        lock (gate)
        {
            current = current with { Libraries = current.Libraries.Select(l => l.Key == libraryKey ? l with { DriveId = driveId } : l).ToList() };
            dirty = true;
        }
    }

    /// <summary>Saves the current state at the next SaveIfChanged (a first start: the OneDrive is known before any discovery).</summary>
    public void Touch()
    {
        lock (gate)
        {
            dirty = true;
        }
    }

    /// <summary>Throws what the save threw; the state is then still due.</summary>
    public void SaveIfChanged()
    {
        DiscoveryState? saving = null;
        lock (gate)
        {
            if (dirty)
            {
                (saving, dirty) = (current, false);
            }
        }
        if (saving is null)
        {
            return;
        }
        try
        {
            save(saving);
        }
        catch
        {
            lock (gate)
            {
                dirty = true;
            }
            throw;
        }
    }
}
