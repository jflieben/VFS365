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

    static string RefreshedPath => Path.Combine(DataDirectory, "refreshed.txt");

    /// <summary>When the user last refreshed from the tray (once a day at most).</summary>
    public static DateTimeOffset? LastRefresh
    {
        get
        {
            try
            {
                return File.Exists(RefreshedPath) && DateTimeOffset.TryParse(File.ReadAllText(RefreshedPath).Trim(), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
        set
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(RefreshedPath, value?.UtcDateTime.ToString("o") ?? "");
        }
    }
}

/// <summary>
/// Whether a refresh from the tray is allowed now: once a day, and only when the libraries were read more than an hour ago.
/// Null when it is; otherwise why not, for the menu's tooltip.
/// </summary>
public static class RefreshRule
{
    public static readonly TimeSpan Once = TimeSpan.FromDays(1), MinimumAge = TimeSpan.FromHours(1);

    public static string? Blocked(DateTimeOffset now, DateTimeOffset librariesReadAt, DateTimeOffset? lastRefresh, bool running)
    {
        if (running)
        {
            return "Refreshing now.";
        }
        if (lastRefresh is { } refreshed && now - refreshed < Once)
        {
            var again = (refreshed + Once).ToLocalTime();
            return $"Refresh works once a day. You refreshed at {refreshed.ToLocalTime():HH:mm}{(refreshed.ToLocalTime().Date == now.ToLocalTime().Date ? "" : " yesterday")}; " +
                $"you can again from {(again.Date == now.ToLocalTime().Date ? "" : "tomorrow ")}{again:HH:mm}.";
        }
        if (now - librariesReadAt < MinimumAge)
        {
            return $"Your libraries were read {(int)Math.Max(1, (now - librariesReadAt).TotalMinutes)} minute(s) ago. " +
                $"Refresh works when that is more than an hour ago: from {(librariesReadAt + MinimumAge).ToLocalTime():HH:mm}.";
        }
        return null;
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
