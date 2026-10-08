using System.Runtime.CompilerServices;
using System.Text;
using Vfs365.Core;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>In-memory drive with Graph's semantics that matter here: item IDs, eTags with If-Match, conflictBehavior=fail.</summary>
sealed class FakeDriveApi : IDriveApi
{
    sealed class Node(DriveItemInfo item, string path, byte[] content)
    {
        public DriveItemInfo Item { get; set; } = item;
        public string Path { get; set; } = path;
        public byte[] Content { get; set; } = content;
    }

    readonly Dictionary<string, Node> nodes = new() { ["root"] = new(Item("root", "root", true, 0, "e0"), "", []) };
    int version;
    public int ListCalls, GetCalls, Downloads, Moves, Deletes;
    public List<(string? ItemId, string Name, string? IfMatch, bool Whole)> Uploads { get; } = [];
    public Func<UploadTarget, Exception?>? FailUpload { get; set; }

    public DriveItemInfo Add(string path, bool folder = false, string content = "abc") => Add(path, folder ? [] : Encoding.UTF8.GetBytes(content), folder);

    public DriveItemInfo Add(string path, byte[] bytes, bool folder = false)
    {
        lock (nodes)
        {
            var node = new Node(Item($"id-{path}", Name(path), folder, bytes.Length, $"e{++version}"), path, bytes);
            nodes[node.Item.Id] = node;
            Changed(node);
            return node.Item;
        }
    }

    /// <summary>A file the change feed never reports (a change the feed missed).</summary>
    public DriveItemInfo AddUnnoticed(string path, string content = "abc")
    {
        lock (nodes)
        {
            var bytes = Encoding.UTF8.GetBytes(content);
            var node = new Node(Item($"id-{path}", Name(path), false, bytes.Length, $"e{++version}"), path, bytes);
            nodes[node.Item.Id] = node;
            return node.Item;
        }
    }

    public string? ContentOf(string path) => Find(path) is { } node ? Encoding.UTF8.GetString(node.Content) : null;

    public byte[]? BytesOf(string path) => Find(path)?.Content;

    public DriveItemInfo? ItemAt(string path) => Find(path)?.Item;

    public IEnumerable<string> Paths => nodes.Values.Where(n => n.Path.Length > 0).Select(n => n.Path).Order();

    /// <summary>Children per listing page; Graph uses up to 1000.</summary>
    public int PageSize { get; set; } = 1000;

    /// <summary>Held before each page after the first, to test reading while pages arrive.</summary>
    public Func<Task>? BeforeNextPage { get; set; }

    public async IAsyncEnumerable<IReadOnlyList<DriveItemInfo>> ListChildrenAsync(string driveId, string folderPath, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref ListCalls);
        List<DriveItemInfo> children;
        lock (nodes)
        {
            children = nodes.Values
                .Where(n => n.Path.Length > 0 && Parent(n.Path).Equals(folderPath, StringComparison.OrdinalIgnoreCase))
                .Select(n => n.Item).OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }
        for (var offset = 0; offset == 0 || offset < children.Count; offset += PageSize)
        {
            if (offset > 0 && BeforeNextPage is { } wait)
            {
                await wait();
            }
            Interlocked.Increment(ref ListPages);
            yield return children.Skip(offset).Take(PageSize).ToList();
        }
    }

    public int ListPages, DeltaCalls;

    /// <summary>Makes every delta link expire (410), as Graph does after a while.</summary>
    public bool ExpireDeltaLinks { get; set; }

    readonly List<DriveChange> changes = [];

    /// <summary>Every delta call fails (503), so listings rely on their TTL.</summary>
    public bool DeltaUnavailable { get; set; }

    /// <summary>Every delta call fails as SharePoint does while a site is read-only (403 serviceReadOnly).</summary>
    public bool DeltaReadOnly { get; set; }

    public Task<string> GetLatestDeltaLinkAsync(string driveId, CancellationToken ct)
    {
        Interlocked.Increment(ref DeltaCalls);
        if (DeltaReadOnly)
        {
            throw new RemoteException(RemoteError.ReadOnly, "403 serviceReadOnly: Database Is Read Only");
        }
        if (DeltaUnavailable)
        {
            throw new RemoteException(RemoteError.Unavailable, "503");
        }
        lock (nodes)
        {
            return Task.FromResult($"delta:{changes.Count}");
        }
    }

    public Task<DeltaPage> GetDeltaAsync(string link, CancellationToken ct)
    {
        Interlocked.Increment(ref DeltaCalls);
        if (DeltaReadOnly)
        {
            throw new RemoteException(RemoteError.ReadOnly, "403 serviceReadOnly: Database Is Read Only");
        }
        if (DeltaUnavailable)
        {
            throw new RemoteException(RemoteError.Unavailable, "503");
        }
        if (ExpireDeltaLinks)
        {
            throw new RemoteException(RemoteError.Gone, "410 resyncRequired");
        }
        lock (nodes)
        {

            var from = int.Parse(link["delta:".Length..]);
            return Task.FromResult(new DeltaPage(changes.Skip(from).ToList(), null, $"delta:{changes.Count}"));
        }
    }

    void Changed(Node node, bool deleted = false)
    {
        lock (nodes)
        {
            changes.Add(new DriveChange(node.Item.Id, ParentIdOf(node.Path), deleted ? null : node.Item, deleted));
        }
    }

    string ParentIdOf(string path) => Find(Parent(path))?.Item.Id ?? "root";

    /// <summary>A change made elsewhere (browser, another device): renames or moves an item.</summary>
    public void RemoteMove(string path, string newPath)
    {
        lock (nodes)
        {
            var node = Find(path)!;
            foreach (var child in nodes.Values.Where(n => n.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase)))
            {
                child.Path = newPath + child.Path[node.Path.Length..];
            }
            node.Path = newPath;
            node.Item = node.Item with { Name = Name(newPath), ETag = $"e{++version}" };
            Changed(node);
        }
    }

    /// <summary>A change made elsewhere: new content for an existing file.</summary>
    public void RemoteEdit(string path, string content)
    {
        lock (nodes)
        {
            var node = Find(path)!;
            node.Content = Encoding.UTF8.GetBytes(content);
            node.Item = node.Item with { Size = node.Content.Length, ETag = $"e{++version}", CTag = $"c{version}" };
            Changed(node);
        }
    }

    /// <summary>A change made elsewhere: the item and everything below it are deleted.</summary>
    public void RemoteDelete(string path)
    {
        lock (nodes)
        {
            var node = Find(path)!;
            foreach (var key in nodes.Where(n => n.Value == node || n.Value.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase)).Select(n => n.Key).ToList())
            {
                Changed(nodes[key], deleted: true);
                nodes.Remove(key);
            }
        }
    }

    public Task<DriveItemInfo?> GetItemAsync(string driveId, string path, CancellationToken ct)
    {
        Interlocked.Increment(ref GetCalls);
        return Task.FromResult(Find(path)?.Item);
    }

    /// <summary>Bytes the engine actually pulled from download streams.</summary>
    public long BytesStreamed => Interlocked.Read(ref bytesStreamed);

    public void AddStreamed(int bytes) => Interlocked.Add(ref bytesStreamed, bytes);
    long bytesStreamed;

    public int Sessions, Fragments, CancelledSessions;
    readonly Dictionary<Uri, (UploadTarget Target, byte[] Buffer)> sessions = [];

    /// <summary>Downloads that fail after starting, like a dropped connection.</summary>
    public int FailDownloads;

    public async Task<Stream> OpenReadAsync(string driveId, string itemId, long offset, long length, CancellationToken ct)
    {
        Interlocked.Increment(ref Downloads);
        await Task.Delay(20, ct);
        var node = nodes[itemId];
        if (Interlocked.Decrement(ref FailDownloads) >= 0)
        {
            throw new IOException("The response ended prematurely.");
        }
        if (node.Content.Length != length)
        {
            throw new ContentChangedException(node.Item with { Size = node.Content.Length }, length);
        }
        return new CountingStream(new MemoryStream(node.Content, (int)offset, node.Content.Length - (int)offset), this);
    }

    /// <summary>New content that the item's size and the change feed don't show (a change not seen yet, or SharePoint rewriting it).</summary>
    public void ServeOtherContent(string path, byte[] content)
    {
        lock (nodes)
        {
            Find(path)!.Content = content;
        }
    }

    /// <summary>How long each whole-file upload takes, like a network would.</summary>
    public TimeSpan UploadDelay { get; set; }

    /// <summary>Held at the start of each whole-file upload, to test what happens meanwhile.</summary>
    public Func<Task>? BeforeUpload { get; set; }

    int uploading;
    public int MaxConcurrentUploads;

    public async Task<DriveItemInfo> UploadAsync(string driveId, UploadTarget target, string sourceFile, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref uploading);
        for (var max = MaxConcurrentUploads; now > max; max = MaxConcurrentUploads)
        {
            Interlocked.CompareExchange(ref MaxConcurrentUploads, now, max);
        }
        try
        {
            if (BeforeUpload is { } hold)
            {
                await hold();
            }
            var bytes = await File.ReadAllBytesAsync(sourceFile, ct);
            if (UploadDelay > TimeSpan.Zero)
            {
                await Task.Delay(UploadDelay, ct);
            }
            return Commit(target, bytes, whole: true);
        }
        finally
        {
            Interlocked.Decrement(ref uploading);
        }
    }

    public Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct)
    {
        Interlocked.Increment(ref Sessions);
        var session = new UploadSession(new Uri($"https://upload.test/{Guid.NewGuid():N}"));
        lock (sessions)
        {
            sessions[session.UploadUrl] = (target, []);
        }
        return Task.FromResult(session);
    }

    public async Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct)
    {
        Interlocked.Increment(ref Fragments);
        await Task.Delay(5, ct);
        var bytes = new byte[length];
        using (var file = File.OpenHandle(sourceFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            RandomAccess.Read(file, bytes, offset);
        }
        lock (sessions)
        {
            var (target, buffer) = sessions[session.UploadUrl];
            if (buffer.Length != totalSize)
            {
                buffer = new byte[totalSize];
            }
            bytes.CopyTo(buffer, offset);
            sessions[session.UploadUrl] = (target, buffer);
            if (offset + length < totalSize)
            {
                return null;
            }
            sessions.Remove(session.UploadUrl);
            return Commit(target, buffer, whole: false);
        }
    }

    public Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct)
    {
        Interlocked.Increment(ref CancelledSessions);
        lock (sessions)
        {
            sessions.Remove(session.UploadUrl);
        }
        return Task.CompletedTask;
    }

    DriveItemInfo Commit(UploadTarget target, byte[] bytes, bool whole)
    {
        lock (nodes)
        {
            return CommitLocked(target, bytes, whole);
        }
    }

    DriveItemInfo CommitLocked(UploadTarget target, byte[] bytes, bool whole)
    {
        if (FailUpload?.Invoke(target) is { } failure)
        {
            throw failure;
        }
        if (target.ItemId is { } id)
        {
            var node = nodes[id];
            if (target.IfMatch is not null && target.IfMatch != node.Item.ETag)
            {
                throw new RemoteException(RemoteError.Conflict, "412 eTag mismatch");
            }
            node.Content = bytes;
            node.Item = node.Item with { Size = bytes.Length, ETag = $"e{++version}", CTag = $"c{version}" };
            Changed(node);
            Uploads.Add((id, node.Item.Name, target.IfMatch, whole));
            return node.Item;
        }

        var path = Join(target.ParentPath!, target.Name!);
        if (Find(path) is not null)
        {
            throw new RemoteException(RemoteError.Conflict, "409 nameAlreadyExists");
        }
        var created = new Node(Item($"id-new-{++version}", target.Name!, false, bytes.Length, $"e{version}"), path, bytes);
        nodes[created.Item.Id] = created;
        Changed(created);
        Uploads.Add((null, target.Name!, null, whole));
        return created.Item;
    }

    public Task<DriveItemInfo> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct)
    {
        var path = Join(parentPath, name);
        return Find(path) is not null ? throw new RemoteException(RemoteError.Conflict, "409 nameAlreadyExists") : Task.FromResult(Add(path, folder: true));
    }

    public Task<DriveItemInfo> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct)
    {
        Interlocked.Increment(ref Moves);
        var node = nodes[itemId];
        var newPath = Join(nodes[newParentId].Path, newName);
        if (Find(newPath) is { } other && other != node)
        {
            throw new RemoteException(RemoteError.Conflict, "409 nameAlreadyExists");
        }
        foreach (var child in nodes.Values.Where(n => n.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase)))
        {
            child.Path = newPath + child.Path[node.Path.Length..];
        }
        node.Path = newPath;
        node.Item = node.Item with { Name = newName, ETag = $"e{++version}" };
        Changed(node);
        return Task.FromResult(node.Item);
    }

    public Task DeleteAsync(string driveId, string itemId, CancellationToken ct)
    {
        Interlocked.Increment(ref Deletes);
        var node = nodes[itemId];
        foreach (var key in nodes.Where(n => n.Value == node || n.Value.Path.StartsWith(node.Path + "/", StringComparison.OrdinalIgnoreCase)).Select(n => n.Key).ToList())
        {
            Changed(nodes[key], deleted: true);
            nodes.Remove(key);
        }
        return Task.CompletedTask;
    }

    Node? Find(string path)
    {
        lock (nodes)
        {
            return nodes.Values.FirstOrDefault(n => n.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        }
    }

    static DriveItemInfo Item(string id, string name, bool folder, long size, string eTag) =>
        new(id, name, folder, size, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, eTag, $"c-{eTag}");

    static string Name(string path) => path[(path.LastIndexOf('/') + 1)..];

    static string Parent(string path) => path.LastIndexOf('/') is var split and >= 0 ? path[..split] : "";

    static string Join(string parent, string name) => parent.Length == 0 ? name : $"{parent}/{name}";
}

sealed class CountingStream(Stream inner, FakeDriveApi api) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    int Count(int read)
    {
        api.AddStreamed(read);
        return read;
    }
}