using System.Collections.Concurrent;
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

    /// <summary>A download stream that brings no bytes for this long is given up (a failed attempt), and so is a read waiting for it.</summary>
    public TimeSpan NoProgressTimeout { get; init; } = TimeSpan.FromSeconds(90);
}

/// <summary>
/// File content on local disk: one file per item version, in a folder per drive (cache\&lt;drive&gt;\&lt;item&gt;-&lt;version&gt;), encrypted and
/// authenticated when a cipher is set and kept under MaxBytes. Content not here yet streams in as it is read. Content written under
/// another key or format, or by a version that didn't encrypt, is dropped at start; a file that fails its check is dropped when read.
/// </summary>
public sealed class ContentCache
{
    const string KeyMarker = "key";

    /// <summary>
    /// A complete cache file: size on disk, the content length it holds (-1 when its size fits no content length), when this run
    /// completed it (earlier runs: MinValue) and when it was last used.
    /// </summary>
    sealed class Entry(long length, long contentLength, DateTime completedAt, DateTime usedAt)
    {
        public long Length { get; } = length;
        public long ContentLength { get; } = contentLength;
        public DateTime CompletedAt { get; } = completedAt;
        public DateTime UsedAt { get; set; } = usedAt;
    }

    readonly string directory;
    readonly IDriveApi api;
    readonly ContentCacheOptions options;
    readonly Dictionary<string, StreamingContent> streaming = new(StringComparer.OrdinalIgnoreCase);
    readonly ConcurrentDictionary<string, string> driveFolders = new(StringComparer.Ordinal);

    /// <summary>Lock order: streaming before sizeGate, never the other way round.</summary>
    readonly Lock sizeGate = new();
    readonly Dictionary<string, Entry> files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The complete files of each item (by the path up to the version), so finding an item's versions needs no disk scan.</summary>
    readonly Dictionary<string, HashSet<string>> versions = new(StringComparer.OrdinalIgnoreCase);
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
                    finished => Completed(path, finished), gaveUp => Failed(path, gaveUp), changed, options.NoProgressTimeout);
                streaming[path] = download;
            }
            download.References++;
            return new Reference(this, path, download);
        }
    }

    /// <summary>
    /// The length of the complete cached content of this version, null when it isn't cached. Reads of it serve that many bytes, which
    /// can differ from the size Graph lists (SharePoint writes properties into Office files as it serves them).
    /// </summary>
    public long? LengthOf(string driveId, string itemId, string? contentTag)
    {
        var path = PathFor(driveId, itemId, contentTag);
        lock (sizeGate)
        {
            return files.TryGetValue(path, out var entry) && entry.ContentLength >= 0 ? entry.ContentLength : null;
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
            Added(path, DateTime.UtcNow);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes every cached version of an item (deleted, or no longer accessible); a download of it still running is dropped when it ends.</summary>
    public void ForgetItem(string driveId, string itemId)
    {
        var item = ItemOf(PathFor(driveId, itemId, null));
        lock (streaming)
        {
            foreach (var (path, download) in streaming)
            {
                if (ItemOf(path).Equals(item, StringComparison.OrdinalIgnoreCase))
                {
                    download.Forgotten = true;
                }
            }
        }
        List<string> cached;
        lock (sizeGate)
        {
            cached = versions.TryGetValue(item, out var known) ? [.. known] : [];
        }
        foreach (var path in cached)
        {
            Remove(path);
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
            versions.Clear();
            foreach (var file in new DirectoryInfo(directory).EnumerateDirectories().SelectMany(d => d.EnumerateFiles()).Where(f => f.Extension != ".part"))
            {
                Track(file.FullName, new Entry(file.Length, ContentLengthOf(file.Length), DateTime.MinValue, file.LastAccessTimeUtc));
            }
            total = files.Values.Sum(f => f.Length);
        }
    }

    /// <summary>Records a complete file. Called under sizeGate; the total is the caller's.</summary>
    void Track(string path, Entry entry)
    {
        files[path] = entry;
        var item = ItemOf(path);
        if (!versions.TryGetValue(item, out var known))
        {
            versions[item] = known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        known.Add(path);
    }

    long ContentLengthOf(long stored) => options.Cipher is null ? stored : CacheCipher.ContentLength(stored);

    string PathFor(string driveId, string itemId, string? contentTag) =>
        Path.Combine(directory, driveFolders.GetOrAdd(driveId, Hash), $"{Hash(itemId)}-{Hash(contentTag ?? "")}");

    /// <summary>A cache file's path up to its version: the same for every cached version of the item.</summary>
    static string ItemOf(string path) => path[..(path.LastIndexOf('-') + 1)];

    static string Hash(string text)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        return Convert.ToHexString(hash[..8]);
    }

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
        if (finished.Forgotten)
        {
            TryDelete(path); // the item was deleted while it downloaded; readers keep their open handle
            return;
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

    /// <summary>
    /// A new complete file, asked for at <paramref name="requestedAt"/> (an adopted upload: now). Versions of the item completed before
    /// then are older and go; one completed later may be newer and stays. Then the cache is trimmed, keeping this file.
    /// </summary>
    void Added(string path, DateTime requestedAt)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }
        var now = DateTime.UtcNow;
        var older = new List<string>();
        lock (sizeGate)
        {
            if (files.TryGetValue(path, out var replaced))
            {
                total -= replaced.Length;
            }
            Track(path, new Entry(info.Length, ContentLengthOf(info.Length), now, now));
            total += info.Length;
            foreach (var other in versions[ItemOf(path)])
            {
                if (!other.Equals(path, StringComparison.OrdinalIgnoreCase) && !(files.TryGetValue(other, out var entry) && entry.CompletedAt >= requestedAt))
                {
                    older.Add(other);
                }
            }
        }
        Touch(path);
        foreach (var other in older)
        {
            Remove(other);
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
        // Chosen under the lock, deleted after it: reads of the cache don't wait for the disk
        var victims = new List<string>();
        lock (sizeGate)
        {
            var target = options.MaxBytes / 10 * 9;
            var left = total;
            foreach (var (path, entry) in files.OrderBy(f => f.Value.UsedAt))
            {
                if (left <= target)
                {
                    break;
                }
                if (!path.Equals(keep, StringComparison.OrdinalIgnoreCase) && !busy.Contains(path))
                {
                    victims.Add(path);
                    left -= entry.Length;
                }
            }
        }
        foreach (var path in victims)
        {
            Remove(path);
        }
    }

    /// <summary>Deletes a complete file; the total drops only when this call removed it.</summary>
    void Remove(string path)
    {
        if (File.Exists(path) && !TryDelete(path))
        {
            return;
        }
        lock (sizeGate)
        {
            if (files.Remove(path, out var entry))
            {
                total -= entry.Length;
            }
            var item = ItemOf(path);
            if (versions.TryGetValue(item, out var known) && known.Remove(path) && known.Count == 0)
            {
                versions.Remove(item);
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
