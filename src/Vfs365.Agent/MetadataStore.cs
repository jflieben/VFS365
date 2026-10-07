using System.Text.Json;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

/// <summary>
/// Folder listings the change feeds kept current, with the delta links to continue from (%LOCALAPPDATA%\VFS365\metadata.bin,
/// sealed with the cache key). The next start shows these folders before asking Graph; the first read of each feed brings them up to date.
/// </summary>
static class MetadataStore
{
    /// <summary>Most recently used folders kept per drive.</summary>
    public const int MaxFoldersPerDrive = 2000;

    public static string FilePath { get; } = Path.Combine(DiscoveryStore.DataDirectory, "metadata.bin");

    public static IReadOnlyList<DriveSnapshot> Load(byte[] key)
    {
        try
        {
            return File.Exists(FilePath) && CacheKey.Unseal(key, File.ReadAllBytes(FilePath)) is { } json
                ? JsonSerializer.Deserialize<List<DriveSnapshot>>(json) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException)
        {
            return [];
        }
    }

    public static void Save(IReadOnlyList<DriveSnapshot> snapshots, byte[] key)
    {
        Directory.CreateDirectory(DiscoveryStore.DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, CacheKey.Seal(key, JsonSerializer.SerializeToUtf8Bytes(snapshots)));
        File.Move(temp, FilePath, overwrite: true);
    }

    public static void Delete()
    {
        File.Delete(FilePath);
        File.Delete(Path.Combine(DiscoveryStore.DataDirectory, "metadata.json"));
    }
}
