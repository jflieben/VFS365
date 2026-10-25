using System.Buffers;
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

    /// <summary>A download whose last reader closed before it completed is kept this long, so an open soon after (thumbnailer, property handler, player) continues it.</summary>
    public TimeSpan ReleasedKeep { get; init; } = TimeSpan.FromSeconds(45);
}

/// <summary>
/// File content on local disk: one file per item version, in a folder per drive (cache\&lt;drive&gt;\&lt;item&gt;-&lt;version&gt;), encrypted and
/// authenticated when a cipher is set and kept under MaxBytes. Content not here yet streams in as it is read. Content written under
/// another key or format, or by a version that didn't encrypt, is dropped at start; a file that fails its check is dropped when read.
/// What is here is known in memory by item version, so opens and lengths need no disk access and no hashing; the name index (the IDs
/// each file name hashes, sealed line by line) carries that to the next start.
/// </summary>
public sealed class ContentCache
{
    const string KeyMarker = "key";
    const string NameIndex = "names";

    /// <summary>A file's last access time on disk (eviction order after a restart) is set when it is used, at most this often.</summary>
    static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(5);

    /// <summary>An item version: drive, item and content tag ("" for none).</summary>
    readonly record struct ContentKey(string DriveId, string ItemId, string Tag);

    /// <summary>
    /// A complete cache file: its name with the drive folder, size on disk, the content length it holds (-1 when its size fits no
    /// content length), when this run completed it (earlier runs: MinValue), when it was last used and when that was last set on disk.
    /// Key is null for a file of an earlier run the name index doesn't list: a lookup hashes its key into the name, then sets it.
    /// </summary>
    sealed class Entry(string path, string name, long length, long contentLength, DateTime completedAt, DateTime usedAt)
    {
        long used = usedAt.Ticks, touched = usedAt.Ticks;

        public string Path { get; } = path;
        public string Name { get; } = name;
        public string Folder => Name[..Name.IndexOf('/')];
        public ContentKey? Key { get; set; }
        public long Length { get; } = length;
        public long ContentLength { get; } = contentLength;
        public DateTime CompletedAt { get; } = completedAt;
        public DateTime UsedAt { get => new(Volatile.Read(ref used), DateTimeKind.Utc); set => Volatile.Write(ref used, value.Ticks); }
        public DateTime TouchedAt { get => new(Volatile.Read(ref touched), DateTimeKind.Utc); set => Volatile.Write(ref touched, value.Ticks); }
    }

    readonly string directory;
    readonly IDriveApi api;
    readonly ContentCacheOptions options;

    /// <summary>Downloads by item version. Held only to look up, add or remove, never across a file system call.</summary>
    readonly Dictionary<ContentKey, StreamingContent> streaming = [];

    /// <summary>Released downloads kept for a reopen, oldest first (entries reopened or dropped since are skipped). Under streaming.</summary>
    readonly Queue<(ContentKey Key, StreamingContent Download, int Release)> kept = new();
    const int MaxKept = 16;
    readonly ConcurrentDictionary<string, string> driveFolders = new(StringComparer.Ordinal);

    /// <summary>Complete files by item version, read without a lock. Changed under sizeGate, with <see cref="files"/>.</summary>
    readonly ConcurrentDictionary<ContentKey, Entry> known = new();

    /// <summary>Lock order: streaming and namesGate before sizeGate, never the other way round.</summary>
    readonly Lock sizeGate = new();
    readonly Dictionary<string, Entry> files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The complete files of each item (by the path up to the version), so finding an item's versions needs no disk scan.</summary>
    readonly Dictionary<string, HashSet<string>> versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files without a key, per drive folder and in all: a lookup hashes only while its drive has any.</summary>
    readonly Dictionary<string, int> unmatchedIn = new(StringComparer.OrdinalIgnoreCase);
    int unmatched;

    /// <summary>Older versions a newer one replaced, deleted by the next trim.</summary>
    readonly HashSet<string> superseded = new(StringComparer.OrdinalIgnoreCase);
    long total;

    readonly Lock namesGate = new();

    /// <summary>Keys listed since the name index was last written, appended by the next trim. Under namesGate.</summary>
    readonly List<ContentKey> unrecorded = [];
    int trimming;
    volatile bool trimAgain;

    public ContentCache(string directory, IDriveApi api, ContentCacheOptions? options = null)
    {
        (this.directory, this.api, this.options) = (Path.GetFullPath(directory), api, options ?? new ContentCacheOptions());
        Directory.CreateDirectory(this.directory);
        var marker = Path.Combine(this.directory, KeyMarker);
        var expected = this.options.Cipher is { } cipher ? $"{cipher.KeyId} {CacheCipher.Format}" : "plain";
        if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != expected)
        {
            Clear();
            foreach (var stray in Directory.EnumerateFiles(this.directory))
            {
                TryDelete(stray);
            }
            File.WriteAllText(marker, expected);
        }
        foreach (var leftover in Directory.EnumerateFiles(this.directory, "*.part", SearchOption.AllDirectories))
        {
            TryDelete(leftover);
        }
        Rescan();
        ScheduleTrim();
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

    /// <summary>Above this a file alone would push out most of the cache: a download isn't kept and an upload isn't adopted.</summary>
    long Target => options.MaxBytes / 10 * 9;

    /// <summary>Above this an upload isn't adopted and a copy (staging for changes) bypasses the cache: big saves don't churn it.</summary>
    long LargeFile => options.MaxBytes / 8;

    /// <summary>
    /// Content for reading: the cached file, else a download that serves each read as soon as its chunks arrive. Dispose when done.
    /// <paramref name="changed"/> gets the item as served when the download isn't that version or isn't <paramref name="length"/> bytes.
    /// </summary>
    public IContentSource Open(string driveId, string itemId, string? contentTag, long length, Action<DriveItemInfo>? changed = null)
    {
        var key = new ContentKey(driveId, itemId, contentTag ?? "");
        StreamingContent? created = null;
        try
        {
            while (true)
            {
                // Opened outside any lock; one gone or damaged is no longer listed afterwards, so this goes on to download it
                if (Find(key) is { } entry && OpenCached(entry) is { } cached)
                {
                    created?.Dispose();
                    return cached;
                }
                StreamingContent? download;
                var landed = false;
                lock (streaming)
                {
                    if (streaming.TryGetValue(key, out download))
                    {
                        if (download.References++ == 0)
                        {
                            download.Reopened(); // a released download kept for a reopen: tried afresh, like a new one
                        }
                    }
                    else if (known.ContainsKey(key))
                    {
                        landed = true; // completed meanwhile: listed before it left the downloads
                    }
                    else if (created is not null)
                    {
                        streaming[key] = download = created;
                        created = null;
                        download.References++;
                    }
                }
                if (download is not null)
                {
                    created?.Dispose(); // another open added one meanwhile
                    return new Reference(this, key, download);
                }
                if (!landed)
                {
                    if (length == 0)
                    {
                        return OpenEmpty(key, contentTag);
                    }
                    created = NewDownload(key, contentTag, length, changed); // its file is made outside the lock
                }
            }
        }
        catch
        {
            created?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The length of the complete cached content of this version, null when it isn't cached. Reads of it serve that many bytes, which
    /// can differ from the size Graph lists (SharePoint writes properties into Office files as it serves them). In memory, no allocation.
    /// </summary>
    public long? LengthOf(string driveId, string itemId, string? contentTag) =>
        Find(new ContentKey(driveId, itemId, contentTag ?? "")) is { ContentLength: >= 0 } entry ? entry.ContentLength : null;

    /// <summary>The content is here, or downloading for a reader (a released download kept for a reopen doesn't count).</summary>
    public bool Has(string driveId, string itemId, string? contentTag)
    {
        var key = new ContentKey(driveId, itemId, contentTag ?? "");
        if (Find(key) is not null)
        {
            return true;
        }
        lock (streaming)
        {
            return streaming.TryGetValue(key, out var download) && download.References > 0;
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

    /// <summary>
    /// Writes the complete content, decrypted, to <paramref name="destination"/>, downloading what is missing and copying it as it
    /// arrives. A large file not cached yet downloads straight into the destination and isn't cached (it is about to change).
    /// </summary>
    public async Task CopyToAsync(string driveId, string itemId, string? contentTag, long length, string destination, CancellationToken ct,
        Action<DriveItemInfo>? changed = null)
    {
        if (length > LargeFile && !Has(driveId, itemId, contentTag))
        {
            using var direct = new StreamingContent(api, driveId, itemId, contentTag, length, destination, "", null, _ => { }, null, changed,
                options.NoProgressTimeout, inPlace: true);
            await direct.CompleteAsync(ct);
            return;
        }
        using var source = Open(driveId, itemId, contentTag, length, changed);
        using (var output = File.OpenHandle(destination, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            if (source.Length > 0)
            {
                var size = (int)Math.Min(StreamingContent.BlockSize, source.Length);
                var buffer = ArrayPool<byte>.Shared.Rent(size);
                try
                {
                    for (long offset = 0; offset < source.Length;)
                    {
                        var read = await source.ReadAsync(offset, buffer.AsMemory(0, size), ct);
                        if (read == 0)
                        {
                            throw new IOException($"Cached content ended at {offset} of {source.Length} bytes");
                        }
                        RandomAccess.Write(output, buffer.AsSpan(0, read), offset);
                        offset += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        if (source is Reference reference)
        {
            await reference.Download.CompleteAsync(ct); // in the cache before this returns
        }
    }

    /// <summary>
    /// Keeps an uploaded file as the cached content of <paramref name="item"/> (encrypting it), unless SharePoint changed it on upload
    /// (Office files) or it is large (MaxBytes/8: encrypting it takes long and pushes out much else). The source file is gone afterwards
    /// either way.
    /// </summary>
    public void Adopt(string driveId, DriveItemInfo item, string file)
    {
        try
        {
            var key = new ContentKey(driveId, item.Id, item.CTag ?? item.ETag ?? "");
            var path = PathFor(key);
            var length = new FileInfo(file).Length;
            if (length != item.Size || length > LargeFile)
            {
                File.Delete(file); // the version before stays: Word's backup copy is made from it
                return;
            }
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
                    Transform(input, output, NameOf(path));
                }
                File.Move(temp, path, overwrite: true);
                File.Delete(file);
            }
            Added(key, path, DateTime.UtcNow);
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
        DropDownloads(key => key.DriveId == driveId && key.ItemId == itemId);
        var item = Path.Combine(directory, FolderOf(driveId), $"{Hash(itemId)}-");
        List<string> cached;
        lock (sizeGate)
        {
            cached = versions.TryGetValue(item, out var paths) ? [.. paths] : [];
        }
        foreach (var path in cached)
        {
            Remove(path);
        }
    }

    /// <summary>Removes everything cached for a drive (a library the user lost or that is no longer shown).</summary>
    public void ForgetDrive(string driveId)
    {
        DropDownloads(key => key.DriveId == driveId);
        var folder = new DirectoryInfo(Path.Combine(directory, FolderOf(driveId)));
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

    /// <summary>Removes all cached content (sign-out, another account, unenrollment). Downloads in progress finish but aren't kept.</summary>
    public void Clear()
    {
        DropDownloads(_ => true);
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

    /// <summary>
    /// Takes matching downloads out, so new opens start afresh: their files aren't kept, readers holding them keep reading, and
    /// released ones nobody holds go now.
    /// </summary>
    void DropDownloads(Func<ContentKey, bool> matches)
    {
        var released = new List<StreamingContent>();
        lock (streaming)
        {
            foreach (var key in streaming.Keys.Where(matches).ToList())
            {
                var download = streaming[key];
                streaming.Remove(key);
                download.Discard = true;
                if (download.References == 0)
                {
                    download.Releases++; // the delayed drop skips it
                    released.Add(download);
                }
            }
        }
        foreach (var download in released)
        {
            download.Dispose();
        }
    }

    /// <summary>The complete file of this version: listed in memory, or (a file of an earlier run the name index missed) found by its hashed name.</summary>
    Entry? Find(ContentKey key) =>
        known.TryGetValue(key, out var entry) ? entry : Volatile.Read(ref unmatched) > 0 ? Match(key) : null;

    /// <summary>A file without a key, found by hashing the key into its name; listed by key from then on (also in the name index).</summary>
    Entry? Match(ContentKey key)
    {
        var folder = FolderOf(key.DriveId);
        lock (sizeGate)
        {
            if (unmatchedIn.GetValueOrDefault(folder) == 0)
            {
                return null;
            }
        }
        var path = PathFor(key);
        Entry? entry;
        lock (sizeGate)
        {
            if (!files.TryGetValue(path, out entry) || entry.Key is not null)
            {
                return entry;
            }
            entry.Key = key;
            known[key] = entry;
            unmatchedIn[entry.Folder]--;
            unmatched--;
        }
        RecordName(key);
        return entry;
    }

    /// <summary>The complete cache file, or null when it is gone or damaged (then it is no longer listed).</summary>
    CachedFile? OpenCached(Entry entry)
    {
        var now = DateTime.UtcNow;
        var touch = now - entry.TouchedAt >= TouchEvery;
        try
        {
            var cached = new CachedFile(this, entry, touch ? now : null);
            entry.UsedAt = now;
            if (touch)
            {
                entry.TouchedAt = now;
            }
            return cached;
        }
        catch (Exception e) when (e is CacheDamagedException or FileNotFoundException or DirectoryNotFoundException)
        {
            Remove(entry.Path, always: true);
            return null;
        }
    }

    /// <summary>An empty file needs no download: it is cached at once, like any other, so it is found again (also after a restart).</summary>
    IContentSource OpenEmpty(ContentKey key, string? contentTag)
    {
        using (var empty = NewDownload(key, contentTag, 0, null))
        {
            empty.Finish();
        }
        return Find(key) is { } entry && OpenCached(entry) is { } cached ? cached : EmptyContent.Instance;
    }

    StreamingContent NewDownload(ContentKey key, string? contentTag, long length, Action<DriveItemInfo>? changed)
    {
        var path = PathFor(key);
        return new StreamingContent(api, key.DriveId, key.ItemId, contentTag, length, path, NameOf(path), options.Cipher,
            finished => Completed(key, finished), gaveUp => Failed(key, gaveUp), changed, options.NoProgressTimeout);
    }

    /// <summary>Counts what is on disk again, with the keys the name index gives.</summary>
    void Rescan()
    {
        var names = ReadNames();
        lock (sizeGate)
        {
            files.Clear();
            versions.Clear();
            known.Clear();
            unmatchedIn.Clear();
            superseded.Clear();
            (unmatched, total) = (0, 0);
            foreach (var folder in new DirectoryInfo(directory).EnumerateDirectories())
            {
                foreach (var file in folder.EnumerateFiles().Where(f => f.Extension != ".part"))
                {
                    var name = $"{folder.Name}/{file.Name}";
                    Track(new Entry(file.FullName, name, file.Length, ContentLengthOf(file.Length), DateTime.MinValue, file.LastAccessTimeUtc)
                    {
                        Key = names.TryGetValue(name, out var key) ? key : null,
                    });
                }
            }
        }
        WriteNames();
    }

    /// <summary>Lists a complete file. Called under sizeGate.</summary>
    void Track(Entry entry)
    {
        files[entry.Path] = entry;
        total += entry.Length;
        superseded.Remove(entry.Path);
        var item = ItemOf(entry.Path);
        if (!versions.TryGetValue(item, out var paths))
        {
            versions[item] = paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        paths.Add(entry.Path);
        if (entry.Key is { } key)
        {
            known[key] = entry;
        }
        else
        {
            unmatchedIn[entry.Folder] = unmatchedIn.GetValueOrDefault(entry.Folder) + 1;
            unmatched++;
        }
    }

    /// <summary>No longer lists a file; false when it wasn't listed. Called under sizeGate.</summary>
    bool Untrack(string path)
    {
        if (!files.Remove(path, out var entry))
        {
            return false;
        }
        total -= entry.Length;
        var item = ItemOf(path);
        if (versions.TryGetValue(item, out var paths) && paths.Remove(path) && paths.Count == 0)
        {
            versions.Remove(item);
        }
        if (entry.Key is { } key)
        {
            known.TryRemove(KeyValuePair.Create(key, entry));
        }
        else
        {
            unmatchedIn[entry.Folder]--;
            unmatched--;
        }
        return true;
    }

    /// <summary>Marks the versions of the file's item completed before <paramref name="requestedAt"/> for the next trim. Called under sizeGate.</summary>
    void Supersede(string path, DateTime requestedAt)
    {
        if (versions.TryGetValue(ItemOf(path), out var paths))
        {
            foreach (var other in paths)
            {
                if (!other.Equals(path, StringComparison.OrdinalIgnoreCase) && !(files.TryGetValue(other, out var entry) && entry.CompletedAt >= requestedAt))
                {
                    superseded.Add(other);
                }
            }
        }
    }

    long ContentLengthOf(long stored) => options.Cipher is null ? stored : CacheCipher.ContentLength(stored);

    string FolderOf(string driveId) => driveFolders.GetOrAdd(driveId, Hash);

    string PathFor(ContentKey key) => Path.Combine(directory, FolderOf(key.DriveId), $"{Hash(key.ItemId)}-{Hash(key.Tag)}");

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

    /// <summary>The name index: the keys of complete files by their names, from lines that open with this key (others are skipped).</summary>
    Dictionary<string, ContentKey> ReadNames()
    {
        var names = new Dictionary<string, ContentKey>(StringComparer.OrdinalIgnoreCase);
        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path.Combine(directory, NameIndex));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return names;
        }
        foreach (var line in lines)
        {
            // The name is worked out from the key, so a line can only ever name the file that key hashes to
            if ((options.Cipher is { } cipher ? cipher.OpenRecord(line) : line)?.Split('\t') is [var driveId, var itemId, var tag])
            {
                var key = new ContentKey(driveId, itemId, tag);
                names[NameOf(PathFor(key))] = key;
            }
        }
        return names;
    }

    /// <summary>Writes the name index anew with the files listed now: what earlier runs added and removed goes.</summary>
    void WriteNames()
    {
        var index = Path.Combine(directory, NameIndex);
        try
        {
            lock (namesGate)
            {
                unrecorded.Clear();
                List<ContentKey> keys;
                lock (sizeGate)
                {
                    keys = [.. files.Values.Where(f => f.Key is not null).Select(f => f.Key!.Value)];
                }
                WriteLines($"{index}.new", FileMode.Create, keys.Select(NameLine).OfType<string>());
                File.Move($"{index}.new", index, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Adds a file's key to the name index (by the next trim, in the background), so the next start knows it without hashing.</summary>
    void RecordName(ContentKey key)
    {
        lock (namesGate)
        {
            unrecorded.Add(key);
        }
        ScheduleTrim();
    }

    void WriteUnrecorded()
    {
        try
        {
            lock (namesGate)
            {
                if (unrecorded.Count > 0)
                {
                    WriteLines(Path.Combine(directory, NameIndex), FileMode.Append, unrecorded.Select(NameLine).OfType<string>());
                    unrecorded.Clear();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Opened so that removing the cache meanwhile (sign-out, Clear) isn't blocked by it.</summary>
    static void WriteLines(string path, FileMode mode, IEnumerable<string> lines)
    {
        using var writer = new StreamWriter(new FileStream(path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        foreach (var line in lines)
        {
            writer.WriteLine(line);
        }
    }

    /// <summary>A key as a name index line, sealed with the cipher; null for IDs a line can't hold (never seen in Graph's).</summary>
    string? NameLine(ContentKey key)
    {
        static bool Fits(string id) => id.AsSpan().IndexOfAny('\t', '\r', '\n') < 0;
        if (!Fits(key.DriveId) || !Fits(key.ItemId) || !Fits(key.Tag))
        {
            return null;
        }
        var text = $"{key.DriveId}\t{key.ItemId}\t{key.Tag}";
        return options.Cipher?.SealRecord(text) ?? text;
    }

    /// <summary>Writes a plain file in the cache format: encrypted and authenticated, or a plain copy without a cipher.</summary>
    void Transform(SafeFileHandle input, SafeFileHandle output, string name)
    {
        var length = RandomAccess.GetLength(input);
        var sealedFile = options.Cipher?.Create(output, length, name);
        if (length == 0)
        {
            return;
        }
        var size = (int)Math.Min(StreamingContent.BlockSize, length);
        var buffer = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            for (long offset = 0; offset < length;)
            {
                var read = 0;
                while (read < size && offset + read < length && RandomAccess.Read(input, buffer.AsSpan(read, size - read), offset + read) is var n and > 0)
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
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    void Completed(ContentKey key, StreamingContent finished)
    {
        // Listed before it leaves the downloads, so an open meanwhile finds one or the other
        if (!finished.Discard)
        {
            Added(key, finished.FinalPath, finished.StartedAt);
        }
        lock (streaming)
        {
            if (streaming.TryGetValue(key, out var current) && current == finished)
            {
                streaming.Remove(key);
            }
        }
        if (finished.Discard)
        {
            Remove(finished.FinalPath, always: true); // the item was deleted while it downloaded; readers keep their open handle
        }
    }

    /// <summary>A download gave up: new readers start a fresh one, readers holding this one keep it.</summary>
    void Failed(ContentKey key, StreamingContent download)
    {
        lock (streaming)
        {
            if (streaming.TryGetValue(key, out var current) && current == download)
            {
                streaming.Remove(key);
            }
        }
    }

    /// <summary>
    /// A new complete file, asked for at <paramref name="requestedAt"/> (an adopted upload: now). Versions of the item completed before
    /// then are older and go; one completed later may be newer and stays. A file above the cache's target alone isn't kept: it would
    /// push out everything else. The cache is trimmed in the background, not on the reader's path.
    /// </summary>
    void Added(ContentKey key, string path, DateTime requestedAt)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }
        var keep = info.Length <= Target;
        var now = DateTime.UtcNow;
        lock (sizeGate)
        {
            Untrack(path);
            if (keep)
            {
                // Its last access time on disk is about when its file was made
                Track(new Entry(path, NameOf(path), info.Length, ContentLengthOf(info.Length), now, now) { Key = key, TouchedAt = requestedAt });
            }
            Supersede(path, requestedAt);
        }
        if (keep)
        {
            RecordName(key);
        }
        else
        {
            TryDelete(path);
            ScheduleTrim();
        }
    }

    /// <summary>Starts a trim in the background unless one runs; one asked for while it runs runs after it.</summary>
    void ScheduleTrim()
    {
        trimAgain = true;
        if (Interlocked.CompareExchange(ref trimming, 1, 0) == 0)
        {
            _ = Task.Run(TrimLoop);
        }
    }

    void TrimLoop()
    {
        do
        {
            trimAgain = false;
            try
            {
                Trim();
            }
            finally
            {
                Volatile.Write(ref trimming, 0);
            }
        }
        while (trimAgain && Interlocked.CompareExchange(ref trimming, 1, 0) == 0);
    }

    /// <summary>
    /// Records new keys in the name index, deletes superseded versions, then least recently used files while the total is above
    /// MaxBytes, down to the target.
    /// </summary>
    void Trim()
    {
        WriteUnrecorded();
        List<string> older;
        lock (sizeGate)
        {
            older = [.. superseded];
            superseded.Clear();
        }
        foreach (var path in older)
        {
            Remove(path);
        }
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
            busy = new HashSet<string>(streaming.Values.Select(d => d.FinalPath), StringComparer.OrdinalIgnoreCase);
        }
        // Chosen under the lock, deleted after it: reads of the cache don't wait for the disk
        var victims = new List<string>();
        lock (sizeGate)
        {
            var left = total;
            foreach (var (path, entry) in files.OrderBy(f => f.Value.UsedAt))
            {
                if (left <= Target)
                {
                    break;
                }
                if (!busy.Contains(path))
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

    /// <summary>
    /// Deletes a complete file; the total drops only when this call removed it. One that can't be deleted stays listed (a later trim
    /// tries again), unless <paramref name="always"/>: then it is never served again this run.
    /// </summary>
    void Remove(string path, bool always = false)
    {
        if (!TryDelete(path) && !always)
        {
            return;
        }
        lock (sizeGate)
        {
            Untrack(path);
        }
    }

    /// <summary>False when the file is still there.</summary>
    static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true; // gone with its folder
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

    /// <summary>
    /// The last reader is done: a complete or dropped download goes now, one still loading is kept a while for a reopen (at most
    /// MaxKept of them, the oldest released goes first).
    /// </summary>
    void Release(ContentKey key, StreamingContent download)
    {
        var release = -1;
        var dropped = new List<StreamingContent>();
        lock (streaming)
        {
            if (--download.References > 0)
            {
                return;
            }
            var listed = streaming.TryGetValue(key, out var current) && current == download;
            if (listed && !download.IsComplete && !download.Discard)
            {
                release = ++download.Releases;
                download.Released();
                kept.Enqueue((key, download, release));
                while (kept.Count > MaxKept)
                {
                    var (oldKey, old, oldRelease) = kept.Dequeue();
                    if (old.References == 0 && old.Releases == oldRelease)
                    {
                        old.Releases++; // the delayed drop skips it
                        if (streaming.TryGetValue(oldKey, out var listedOld) && listedOld == old)
                        {
                            streaming.Remove(oldKey);
                        }
                        dropped.Add(old);
                    }
                }
            }
            else if (listed)
            {
                streaming.Remove(key);
            }
        }
        foreach (var old in dropped)
        {
            old.Dispose();
        }
        if (release < 0)
        {
            download.Dispose();
            return;
        }
        _ = DropLaterAsync(key, download, release);
    }

    /// <summary>Drops a released download after ReleasedKeep, unless it was opened again meanwhile.</summary>
    async Task DropLaterAsync(ContentKey key, StreamingContent download, int release)
    {
        await Task.Delay(options.ReleasedKeep).ConfigureAwait(false);
        lock (streaming)
        {
            if (download.References > 0 || download.Releases != release)
            {
                return; // opened again (a later release drops it), or dropped already
            }
            download.Releases++;
            if (streaming.TryGetValue(key, out var current) && current == download)
            {
                streaming.Remove(key);
            }
        }
        download.Dispose();
    }

    /// <summary>A complete cache file, decrypted and checked as it is read. A file that fails the check is no longer listed.</summary>
    sealed class CachedFile : IContentSource
    {
        readonly SafeFileHandle file;
        readonly CacheCipher.SealedFile? sealedFile;
        readonly ContentCache cache;
        readonly string path;

        /// <param name="touch">When set, the file's last access time on disk becomes this (through the handle).</param>
        public CachedFile(ContentCache cache, Entry entry, DateTime? touch)
        {
            file = CacheFileHandle.OpenRead(entry.Path, touch);
            try
            {
                sealedFile = cache.options.Cipher?.Open(file, entry.Name);
            }
            catch
            {
                file.Dispose();
                throw;
            }
            (this.cache, path) = (cache, entry.Path);
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
                cache.Remove(path, always: true);
                throw;
            }
        }

        public void Dispose() => file.Dispose();
    }

    /// <summary>One reader's handle on a shared download.</summary>
    sealed class Reference(ContentCache cache, ContentKey key, StreamingContent download) : IContentSource
    {
        int released;

        public StreamingContent Download { get; } = download;

        public long Length => Download.Length;

        public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => Download.ReadAsync(offset, buffer, ct);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) == 0)
            {
                cache.Release(key, Download);
            }
        }
    }
}
