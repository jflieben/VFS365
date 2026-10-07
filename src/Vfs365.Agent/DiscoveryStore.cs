using System.Text.Json;
using Vfs365.Core.Discovery;

namespace Vfs365.Agent;

/// <summary>Discovery state per user in %LOCALAPPDATA%\VFS365\discovery.json.</summary>
static class DiscoveryStore
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VFS365");

    public static string FilePath { get; } = Path.Combine(DataDirectory, "discovery.json");

    /// <summary>Null when missing or unreadable: discovery then starts fresh.</summary>
    public static DiscoveryState? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<DiscoveryState>(File.ReadAllText(FilePath), Json) : null;
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

/// <summary>The mounted session's discovery state: replaced by background discovery, amended with drive IDs looked up on first use, saved when changed.</summary>
sealed class DiscoveryStateKeeper(DiscoveryState initial)
{
    readonly Lock gate = new();
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

    /// <summary>A new discovery result; drive IDs learned meanwhile are kept.</summary>
    public void Replace(DiscoveryState state)
    {
        lock (gate)
        {
            var known = current.Libraries.Where(l => l.DriveId is not null).ToDictionary(l => l.Key, l => l.DriveId!, StringComparer.OrdinalIgnoreCase);
            current = state with { Libraries = state.Libraries.Select(l => l.DriveId is null && known.TryGetValue(l.Key, out var id) ? l with { DriveId = id } : l).ToList() };
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

    public void SaveIfChanged()
    {
        DiscoveryState? save = null;
        lock (gate)
        {
            if (dirty)
            {
                (save, dirty) = (current, false);
            }
        }
        if (save is not null)
        {
            DiscoveryStore.Save(save);
        }
    }
}
