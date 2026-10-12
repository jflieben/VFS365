using System.Globalization;
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
                return File.Exists(RefreshedPath) && DateTimeOffset.TryParse(File.ReadAllText(RefreshedPath).Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal, out var at) ? at : null;
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
/// Null when it is; otherwise why not, for the menu's hint, in the display language (or <c>culture</c>).
/// </summary>
public static class RefreshRule
{
    public static readonly TimeSpan Once = TimeSpan.FromDays(1), MinimumAge = TimeSpan.FromHours(1);

    public static string? Blocked(DateTimeOffset now, DateTimeOffset librariesReadAt, DateTimeOffset? lastRefresh, bool running, CultureInfo? culture = null)
    {
        if (running)
        {
            return UiText.Get(culture, "Refresh.Running");
        }
        if (lastRefresh is { } refreshed && now - refreshed < Once)
        {
            return UiText.Get(culture, "Refresh.Daily", $"{refreshed.ToLocalTime():HH:mm}",
                refreshed.ToLocalTime().Date == now.ToLocalTime().Date ? "" : UiText.Get(culture, "Refresh.Yesterday"), At(now, refreshed + Once, culture));
        }
        if (now - librariesReadAt < MinimumAge)
        {
            return UiText.Get(culture, "Refresh.Recent", (int)Math.Max(1, (now - librariesReadAt).TotalMinutes), At(now, librariesReadAt + MinimumAge, culture));
        }
        return null;
    }

    /// <summary>The menu item's text: "Refresh", or with when it works again or that it runs, so the reason doesn't depend on the hint.</summary>
    public static string Label(DateTimeOffset now, DateTimeOffset librariesReadAt, DateTimeOffset? lastRefresh, bool running, CultureInfo? culture = null)
    {
        if (running)
        {
            return UiText.Get(culture, "Menu.RefreshRunning");
        }
        var from = new[] { lastRefresh is { } refreshed && now - refreshed < Once ? refreshed + Once : now, now - librariesReadAt < MinimumAge ? librariesReadAt + MinimumAge : now }.Max();
        return from > now ? UiText.Get(culture, "Menu.RefreshFrom", At(now, from, culture)) : UiText.Get(culture, "Menu.Refresh");
    }

    static string At(DateTimeOffset now, DateTimeOffset at, CultureInfo? culture) =>
        at.ToLocalTime().Date == now.ToLocalTime().Date ? $"{at.ToLocalTime():HH:mm}" : UiText.Get(culture, "Tomorrow", $"{at.ToLocalTime():HH:mm}");
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

    /// <summary>Saves the current state at the next SaveIfChanged (a first start: the OneDrive is known before any discovery).</summary>
    public void Touch()
    {
        lock (gate)
        {
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
