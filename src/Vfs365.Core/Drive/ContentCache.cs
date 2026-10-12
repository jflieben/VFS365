using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Vfs365.Core.Drive;

public sealed record ContentCacheOptions
{
    /// <summary>Complete files beyond this total are evicted, least recently used first.</summary>
    public long MaxBytes { get; init; } = 2L << 30;

    /// <summary>Encrypts content at rest; null keeps it plain (tests).</summary>
    public CacheCipher? Cipher { get; init; }
}

/// <summary>
/// File content on local disk: one file per item version, in a folder per drive (cache\&lt;drive&gt;\&lt;item&gt;-&lt;version&gt;), encrypted and
/// authenticated when a cipher is set and kept under MaxBytes. Content not here yet streams in as it is read. Content written under
/// another key or format, or by a version that didn't encrypt, is dropped at start; a file that fails its check is dropped when read.
/// </summary>
public sealed class ContentCache
{
    const string KeyMarker = "key";

    /// <summary>A complete cache file: size on disk, when this run completed it (earlier runs: MinValue) and when it was last used.</summary>
    sealed class Entry(long length, DateTime completedAt, DateTime usedAt)
    {
        public long Length { get; } = length;
        public DateTime CompletedAt { get; } = completedAt;
        public DateTime UsedAt { get; set; } = usedAt;
    }

    readonly string directory;
    readonly IDriveApi api;
    readonly ContentCacheOptions options;
    readonly Dictionary<string, StreamingContent> streaming = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lock order: streaming before sizeGate, never the other way round.</summary>
    readonly Lock sizeGate = new();
    readonly Dictionary<string, Entry> files = new(StringComparer.OrdinalIgnoreCase);
    long total;

    public ContentCache(string directory, IDriveApi api, ContentCacheOptions? options = null)
    {
        (this.directory, this.api, this.options) = (directory, api, options ?? new ContentCacheOptions());
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, KeyMarker);
        var expected = this.options.Cipher is { } cipher ? $"{cipher.KeyId} {CacheCipher.Format}" : "plain";
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != expected)
        {
            Clear();
            foreach (var stray in Directory.EnumerateFiles(directory))
            {
                TryDelete(stray);
            }
            File.WriteAllText(marker, expected);
        }
        foreach (var leftover in Directory.EnumerateFiles(directory, "*.part", SearchOption.AllDirectories))
        {
            TryDelete(leftover);
        }
        Rescan();
        Evict(null);
    }

    /// <summary>Bytes and files of complete content on disk.</summary>
    public (long Bytes, int Files) Usage
    {
        get
        {
            lock (sizeGate)
            {
                return (total, files.Count);
            }
        }
    }

    /// <summary>
    /// Content for reading: the cached file, else a download that serves each read as soon as its blocks arrive. Dispose when done.
    /// <paramref name="changed"/> gets the item as served when the download isn't that version or isn't <paramref name="length"/> bytes.
    /// </summary>
    public IContentSource Open(string driveId, string itemId, string? contentTag, long length, Action<DriveItemInfo>? changed = null)
    {
        var path = PathFor(driveId, itemId, contentTag);
        lock (streaming)
        {
            if (!streaming.TryGetValue(path, out var download))
            {
                if (File.Exists(path) && OpenCached(path) is { } cached)
                {
                    return cached;
                }
                download = new StreamingContent(api, driveId, itemId, contentTag, length, path, NameOf(path), options.Cipher,
                    finished => Completed(path, finished), gaveUp => Failed(path, gaveUp), changed);
                streaming[path] = download;
            }
            download.References++;
            return new Reference(this, path, download);
        }
    }

    /// <summary>The complete cache file, or null when it is gone or damaged (then it is removed).</summary>
    CachedFile? OpenCached(string path)
    {
        try
        {
            var cached = new CachedFile(path, options.Cipher, () => Remove(path));
            Touch(path);
            return cached;
        }
        catch (CacheDamagedException)
        {
            Remove(path);
            return null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The content is here, or downloading.</summary>
    public bool Has(string driveId, string itemId, string? contentTag)
    {
        var path = PathFor(driveId, itemId, contentTag);
        lock (streaming)
        {
            return streaming.ContainsKey(path) || File.Exists(path);
        }
    }

    /// <summary>Downloads the complete content into the cache, unless it is there.</summary>
    public async Task FetchAsync(string driveId, string itemId, string? contentTag, long length, CancellationToken ct, Action<DriveItemInfo>? changed = null)
    {
        using var source = Open(driveId, itemId, contentTag, length, changed);
        if (source is Reference reference)
        {
            await reference.Download.CompleteAsync(ct);
        }
    }

    /// <summary>Writes the complete content, decrypted, to <paramref name="destination"/>, downloading what is missing.</summary>
    public async Task CopyToAsync(string driveId, string itemId, string? contentTag, long length, string destination, CancellationToken ct,
        Action<DriveItemInfo>? changed = null)
    {
        using var source = Open(driveId, itemId, contentTag, length, changed);
        if (source is Reference reference)
        {
            await reference.Download.CompleteAsync(ct);
        }
        using var output = File.OpenHandle(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[StreamingContent.BlockSize];
        for (long offset = 0; offset < source.Length;)
        {
            var read = await source.ReadAsync(offset, buffer, ct);
            if (read == 0)
            {
                throw new IOException($"Cached content ended at {offset} of {source.Length} bytes");
            }
            RandomAccess.Write(output, buffer.AsSpan(0, read), offset);
            offset += read;
        }
    }

    /// <summary>
    /// Keeps an uploaded file as the cached content of <paramref name="item"/> (encrypting it), unless SharePoint changed it on upload
    /// (Office files). The source file is gone afterwards either way.
    /// </summary>
    public void Adopt(string driveId, DriveItemInfo item, string file)
    {
        try
        {
            var length = new FileInfo(file).Length;
            if (length != item.Size)
            {
                File.Delete(file);
                return;
            }
            var path = PathFor(driveId, item.Id, item.CTag ?? item.ETag);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (options.Cipher is null)
            {
                File.Move(file, path, overwrite: true);
            }
            else
            {
                var temp = $"{path}.{Guid.NewGuid():N}.part";
                using (var input = File.OpenHandle(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var output = File.OpenHandle(temp, FileMode.CreateNew, FileAccess.Write))
                {
                    Transform(input, output, Nonce(path));
                }
                File.Move(temp, path, overwrite: true);
                File.Delete(file);
            }
            Added(path, length);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes every cached version of an item (deleted, or no longer accessible).</summary>
    public void ForgetItem(string driveId, string itemId)
    {
        var folder = Path.Combine(directory, Hash(driveId));
        if (Directory.Exists(folder))
        {
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles($"{Hash(itemId)}-*").Where(f => f.Extension != ".part"))
            {
                Remove(file.FullName);
            }
        }
    }

    /// <summary>Removes everything cached for a drive (a library the user lost or that is no longer shown).</summary>
    public void ForgetDrive(string driveId)
    {
        var folder = new DirectoryInfo(Path.Combine(directory, Hash(driveId)));
        if (folder.Exists)
        {
            foreach (var file in folder.EnumerateFiles().Where(f => f.Extension != ".part"))
            {
                Remove(file.FullName);
            }
            try
            {
                folder.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Removes all cached content (sign-out, another account, unenrollment). Downloads in progress finish into an empty cache.</summary>
    public void Clear()
    {
        foreach (var folder in new DirectoryInfo(directory).EnumerateDirectories())
        {
            try
            {
                folder.Delete(recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
        Rescan();
    }

    /// <summary>Counts what is on disk again.</summary>
    void Rescan()
    {
        lock (sizeGate)
        {
            files.Clear();
            foreach (var file in new DirectoryInfo(directory).EnumerateDirectories().SelectMany(d => d.EnumerateFiles()).Where(f => f.Extension != ".part"))
            {
                files[file.FullName] = new Entry(file.Length, DateTime.MinValue, file.LastAccessTimeUtc);
            }
            total = files.Values.Sum(f => f.Length);
        }
    }

    string PathFor(string driveId, string itemId, string? contentTag) =>
        Path.Combine(directory, Hash(driveId), $"{Hash(itemId)}-{Hash(contentTag ?? "")}");

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    /// <summary>A cache file's name with its drive folder: what its encrypted chunks are bound to.</summary>
    static string NameOf(string path) => $"{Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}";

    /// <summary>What ties a cache file's content to its path: the name its chunks are authenticated with (the nonce itself is random per file).</summary>
    static byte[] Nonce(string path) => Encoding.UTF8.GetBytes(NameOf(path));

    /// <summary>Writes a plain file in the cache format: encrypted and authenticated, or a plain copy without a cipher.</summary>
    void Transform(SafeFileHandle input, SafeFileHandle output, byte[] name)
    {
        var length = RandomAccess.GetLength(input);
        var sealedFile = options.Cipher?.Create(output, length, Encoding.UTF8.GetString(name));
        var buffer = new byte[StreamingContent.BlockSize];
        for (long offset = 0; offset < length;)
        {
            var read = 0;
            while (read < buffer.Length && offset + read < length && RandomAccess.Read(input, buffer.AsSpan(read), offset + read) is var n and > 0)
            {
                read += n;
            }
            if (read == 0)
            {
                throw new IOException("The file shrank while it was cached");
            }
            if (sealedFile is not null)
            {
                sealedFile.Write(offset, buffer.AsSpan(0, read));
            }
            else
            {
                RandomAccess.Write(output, buffer.AsSpan(0, read), offset);
            }
            offset += read;
        }
    }

    void Completed(string path, StreamingContent finished)
    {
        lock (streaming)
        {
            if (streaming.TryGetValue(path, out var current) && current == finished)
            {
                streaming.Remove(path);
            }
        }
        Added(path, finished.StartedAt);
    }

    /// <summary>A download gave up: new readers start a fresh one, readers holding this one keep it.</summary>
    void Failed(string path, StreamingContent download)
    {
        lock (streaming)
        {
            if (streaming.TryGetValue(path, out var current) && current == download)
            {
                streaming.Remove(path);
            }
        }
    }

    /// <summary>A file adopted now: it is the newest version.</summary>
    void Added(string path, long length) => Added(path, DateTime.UtcNow);

    /// <summary>
    /// A new complete file, asked for at <paramref name="requestedAt"/>. Versions of the item completed before then are older and go;
    /// one completed later may be newer and stays. Then the cache is trimmed, keeping this file.
    /// </summary>
    void Added(string path, DateTime requestedAt)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }
        var now = DateTime.UtcNow;
        lock (sizeGate)
        {
            if (files.TryGetValue(path, out var replaced))
            {
                total -= replaced.Length;
            }
            files[path] = new Entry(info.Length, now, now);
            total += info.Length;
        }
        Touch(path);
        var item = info.Name[..info.Name.IndexOf('-')];
        foreach (var other in info.Directory!.EnumerateFiles($"{item}-*").Where(f => f.Name != info.Name && f.Extension != ".part").Select(f => f.FullName).ToList())
        {
            bool newer;
            lock (sizeGate)
            {
                newer = files.TryGetValue(other, out var entry) && entry.CompletedAt >= requestedAt;
            }
            if (!newer)
            {
                Remove(other);
            }
        }
        Evict(path);
    }

    void Evict(string? keep)
    {
        lock (sizeGate)
        {
            if (total <= options.MaxBytes)
            {
                return;
            }
        }
        HashSet<string> busy;
        lock (streaming)
        {
            busy = new HashSet<string>(streaming.Keys, StringComparer.OrdinalIgnoreCase);
        }
        lock (sizeGate)
        {
            var target = options.MaxBytes / 10 * 9;
            foreach (var path in files.OrderBy(f => f.Value.UsedAt).Select(f => f.Key).ToList())
            {
                if (total <= target)
                {
                    return;
                }
                if (!path.Equals(keep, StringComparison.OrdinalIgnoreCase) && !busy.Contains(path))
                {
                    Remove(path);
                }
            }
        }
    }

    /// <summary>Deletes a complete file; the total drops only when this call removed it.</summary>
    void Remove(string path)
    {
        lock (sizeGate)
        {
            if (File.Exists(path) && !TryDelete(path))
            {
                return;
            }
            if (files.Remove(path, out var entry))
            {
                total -= entry.Length;
            }
        }
    }

    /// <summary>Recency for eviction, also set on the file so it lasts to the next start (Windows may not update last-access times).</summary>
    void Touch(string path)
    {
        var now = DateTime.UtcNow;
        lock (sizeGate)
        {
            if (files.TryGetValue(path, out var entry))
            {
                entry.UsedAt = now;
            }
        }
        try
        {
            File.SetLastAccessTimeUtc(path, now);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    void Release(string path, StreamingContent download)
    {
        lock (streaming)
        {
            if (--download.References > 0)
            {
                return;
            }
            if (streaming.TryGetValue(path, out var current) && current == download)
            {
                streaming.Remove(path);
            }
        }
        download.Dispose();
    }

    /// <summary>A complete cache file, decrypted and checked as it is read. A file that fails the check is removed.</summary>
    sealed class CachedFile : IContentSource
    {
        readonly SafeFileHandle file;
        readonly CacheCipher.SealedFile? sealedFile;
        readonly Action damaged;

        public CachedFile(string path, CacheCipher? cipher, Action damaged)
        {
            file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                sealedFile = cipher?.Open(file, NameOf(path));
            }
            catch
            {
                file.Dispose();
                throw;
            }
            this.damaged = damaged;
            Length = sealedFile?.Length ?? RandomAccess.GetLength(file);
        }

        public long Length { get; }

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset >= Length || buffer.Length == 0)
            {
                return ValueTask.FromResult(0);
            }
            var span = buffer.Span[..(int)Math.Min(buffer.Length, Length - offset)];
            try
            {
                return ValueTask.FromResult(sealedFile?.Read(offset, span) ?? RandomAccess.Read(file, span, offset));
            }
            catch (CacheDamagedException)
            {
                damaged();
                throw;
            }
        }

        public void Dispose() => file.Dispose();
    }

    /// <summary>One reader's handle on a shared download.</summary>
    sealed class Reference(ContentCache cache, string path, StreamingContent download) : IContentSource
    {
        int released;

        public StreamingContent Download { get; } = download;

        public long Length => Download.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => Download.ReadAsync(offset, buffer, ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                cache.Release(path, Download);
            }
        }
    }
}
