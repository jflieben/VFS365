using System.Text.Json;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

/// <summary>
/// Folder listings the change feeds kept current, with the delta links to continue from (%LOCALAPPDATA%\VFS365\metadata.bin: JSON
/// in a <see cref="SealedStream"/>, under a key derived from the cache key). The next start shows these folders before asking Graph;
/// the first read of each feed brings them up to date. Written and read as a stream: a large snapshot is never in memory as a whole.
/// </summary>
static class MetadataStore
{
    /// <summary>Most recently used folders kept per drive.</summary>
    public const int MaxFoldersPerDrive = 2000;

    public static string FilePath { get; } = Path.Combine(DiscoveryStore.DataDirectory, "metadata.bin");

    /// <summary>
    /// Empty when the file is missing, damaged, incomplete or another key's: the folders are then listed again when opened.
    /// <paramref name="path"/>: a file elsewhere (tests).
    /// </summary>
    public static IReadOnlyList<DriveSnapshot> Load(byte[] key, string? path = null)
    {
        path ??= FilePath;
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1, FileOptions.SequentialScan);
            Span<byte> start = stackalloc byte[8];
            var marked = file.ReadAtLeast(start, start.Length, throwOnEndOfStream: false) == start.Length && SealedStream.HasMarker(start);
            file.Position = 0;
            if (!marked)
            {
                return LoadOld(file, key);
            }
            using var content = SealedStream.Open(file, key);
            var snapshots = JsonSerializer.Deserialize<List<DriveSnapshot>>(content);
            // Only a file read to its checked end is whole
            return snapshots is not null && content.ReadByte() < 0 ? snapshots : [];
        }
        catch (Exception e) when (e is JsonException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>A file written before 0.9.0: sealed in one piece with the cache key itself. The next save replaces it.</summary>
    static IReadOnlyList<DriveSnapshot> LoadOld(FileStream file, byte[] key)
    {
        var sealedData = new byte[file.Length];
        file.ReadExactly(sealedData);
        return CacheKey.Unseal(key, sealedData) is { } json ? JsonSerializer.Deserialize<List<DriveSnapshot>>(json) ?? [] : [];
    }

    /// <summary>Written to a temporary file chunk by chunk, then renamed into place. <paramref name="path"/>: a file elsewhere (tests).</summary>
    public static void Save(IReadOnlyList<DriveSnapshot> snapshots, byte[] key, string? path = null)
    {
        path ??= FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1))
        using (var content = SealedStream.Create(file, key))
        {
            JsonSerializer.Serialize(content, snapshots);
            content.Complete();
        }
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete()
    {
        File.Delete(FilePath);
        File.Delete(Path.Combine(DiscoveryStore.DataDirectory, "metadata.json"));
    }
}
