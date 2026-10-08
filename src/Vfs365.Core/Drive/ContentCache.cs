using System.Security.Cryptography;
using System.Text;

namespace Vfs365.Core.Drive;

public sealed record ContentCacheOptions
{
    /// <summary>Complete files beyond this total are evicted, least recently used first.</summary>
    public long MaxBytes { get; init; } = 2L << 30;

    /// <summary>Encrypts content at rest; null keeps it plain (tests).</summary>
    public CacheCipher? Cipher { get; init; }
}

/// <summary>
/// File content on local disk: one file per item version, in a folder per drive (cache\&lt;drive&gt;\&lt;item&gt;-&lt;version&gt;), encrypted when a
/// cipher is set and kept under MaxBytes. Content not here yet streams in as it is read. Content written under another key, or by
/// a version that didn't encrypt, is dropped at start.
/// </summary>
public sealed class ContentCache
{
    const string KeyMarker = "key";

    readonly string directory;
    readonly IDriveApi api;
    readonly ContentCacheOptions options;
    readonly Dictionary<string, StreamingContent> streaming = new(StringComparer.OrdinalIgnoreCase);
    readonly Lock sizeGate = new();
    long total;

    public ContentCache(string directory, IDriveApi api, ContentCacheOptions? options = null)
    {
        (this.directory, this.api, this.options) = (directory, api, options ?? new ContentCacheOptions());
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, KeyMarker);
        var expected = this.options.Cipher?.KeyId ?? "plain";
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
        total = CompleteFiles().Sum(f => f.Length);
        Evict(null);
    }


    /// <summary>Bytes and files of complete content on disk.</summary>
    public (long Bytes, int Files) Usage
    {
        get
        {
            lock (sizeGate)
            {
                return (total, CompleteFiles().Count());
            }
        }
    }

    /// <summary>
    /// Content for reading: the cached file, else a download that serves each read as soon as its blocks arrive. Dispose when done.
    /// <paramref name="changed"/> gets the item as served when the download isn't <paramref name="length"/> bytes.
    /// </summary>
    public IContentSource Open(string driveId, string itemId, string? contentTag, long length, Action<DriveItemInfo>? changed = null)
    {
        var path = PathFor(driveId, itemId, contentTag);
        lock (streaming)
        {
            if (!streaming.TryGetValue(path, out var download))
            {
                if (File.Exists(path))
                {
                    Touch(path);
                    return new CachedFile(path, options.Cipher);
                }
                download = new StreamingContent(api, driveId, itemId, length, path, options.Cipher, finished => Completed(path, finished), changed);
                streaming[path] = download;
            }
            download.References++;
            return new Reference(this, path, download);
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
        var path = PathFor(driveId, itemId, contentTag);
        using var source = Open(driveId, itemId, contentTag, length, changed);
        if (source is Reference reference)
        {
            await reference.Download.CompleteAsync(ct);
        }
        using var input = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.OpenHandle(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        Transform(input, output, Nonce(path));
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
                Remove(file);
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
                Remove(file);
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
        lock (sizeGate)
        {
            total = CompleteFiles().Sum(f => f.Length);
        }
    }

    string PathFor(string driveId, string itemId, string? contentTag) =>
        Path.Combine(directory, Hash(driveId), $"{Hash(itemId)}-{Hash(contentTag ?? "")}");

    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    static byte[] Nonce(string path) => CacheCipher.NonceFor(Path.GetFileName(path));

    IEnumerable<FileInfo> CompleteFiles() => new DirectoryInfo(directory).EnumerateDirectories().SelectMany(d => d.EnumerateFiles()).Where(f => f.Extension != ".part");

    /// <summary>Copies a file through the cipher (encrypting or decrypting; plain copy without one).</summary>
    void Transform(Microsoft.Win32.SafeHandles.SafeFileHandle input, Microsoft.Win32.SafeHandles.SafeFileHandle output, byte[] nonce)
    {
        var buffer = new byte[StreamingContent.BlockSize];
        for (long offset = 0; ;)
        {
            var read = RandomAccess.Read(input, buffer, offset);
            if (read == 0)
            {
                return;
            }
            options.Cipher?.Apply(buffer.AsSpan(0, read), offset, nonce);
            RandomAccess.Write(output, buffer.AsSpan(0, read), offset);
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
        if (File.Exists(path))
        {
            Added(path, new FileInfo(path).Length);
        }
    }

    /// <summary>A new complete file: older versions of the item go, then the cache is trimmed (keeping this file).</summary>
    void Added(string path, long length)
    {
        Touch(path);
        lock (sizeGate)
        {
            total += length;
        }
        var name = Path.GetFileName(path);
        var item = name[..name.IndexOf('-')];
        foreach (var older in new DirectoryInfo(Path.GetDirectoryName(path)!).EnumerateFiles($"{item}-*").Where(f => f.Name != name && f.Extension != ".part"))
        {
            Remove(older);
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
            var target = options.MaxBytes / 10 * 9;
            foreach (var file in CompleteFiles().OrderBy(f => f.LastAccessTimeUtc).ToList())
            {
                if (total <= target)
                {
                    return;
                }
                if (!file.FullName.Equals(keep, StringComparison.OrdinalIgnoreCase) && !IsStreaming(file.FullName))
                {
                    Remove(file);
                }
            }
        }
    }

    bool IsStreaming(string path)
    {
        lock (streaming)
        {
            return streaming.ContainsKey(path);
        }
    }

    void Remove(FileInfo file)
    {
        if (TryDelete(file.FullName))
        {
            lock (sizeGate)
            {
                total -= file.Length;
            }
        }
    }

    /// <summary>Recency for eviction, set explicitly (Windows may not update last-access times).</summary>
    static void Touch(string path)
    {
        try
        {
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
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

    /// <summary>A complete cache file, decrypted as it is read.</summary>
    sealed class CachedFile(string path, CacheCipher? cipher) : IContentSource
    {
        readonly Microsoft.Win32.SafeHandles.SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        readonly byte[] nonce = Nonce(path);

        public long Length => RandomAccess.GetLength(file);

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct)
        {
            if (offset >= Length)
            {
                return ValueTask.FromResult(0);
            }
            var read = RandomAccess.Read(file, buffer.Span, offset);
            cipher?.Apply(buffer.Span[..read], offset, nonce);
            return ValueTask.FromResult(read);
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
