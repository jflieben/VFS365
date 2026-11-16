using System.Text.Json;

namespace Vfs365.Agent;

/// <summary>Site and library URLs the user pinned with 'vfs365 pin' (%LOCALAPPDATA%\VFS365\pinned.json), shown next to the admin's PinnedLocations.</summary>
static class UserPins
{
    public static string FilePath { get; } = Path.Combine(DiscoveryStore.DataDirectory, "pinned.json");

    public static IReadOnlyList<string> Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(FilePath)) ?? [] : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }

    /// <summary>The list changed after <paramref name="time"/> (a discovery run's): that run doesn't know it yet.</summary>
    public static bool ChangedSince(DateTimeOffset time)
    {
        try
        {
            return File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) > time.UtcDateTime;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The address `pin` accepts (an absolute https URL), or null.</summary>
    public static Uri? Parse(string? text) => Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null;

    /// <summary>True when the list changed.</summary>
    public static bool Add(string url) => Update(pins => pins.Contains(url, StringComparer.OrdinalIgnoreCase) ? null : [.. pins, url]);

    public static bool Remove(string url) => Update(pins => pins.Contains(url, StringComparer.OrdinalIgnoreCase)
        ? pins.Where(p => !p.Equals(url, StringComparison.OrdinalIgnoreCase)).ToList()
        : null);

    static bool Update(Func<IReadOnlyList<string>, List<string>?> change)
    {
        if (change(Load()) is not { } updated)
        {
            return false;
        }
        Directory.CreateDirectory(DiscoveryStore.DataDirectory);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, FilePath, overwrite: true);
        return true;
    }
}
