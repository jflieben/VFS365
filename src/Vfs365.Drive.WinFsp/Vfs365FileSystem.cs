using System.Buffers;
using System.Security.AccessControl;
using System.Security.Principal;
using Fsp;
using Microsoft.Win32.SafeHandles;
using Vfs365.Core;
using Vfs365.Core.Drive;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Vfs365.Drive.WinFsp;

/// <summary>WinFsp callbacks over the engine. Content lives in local files (downloaded or staged); the engine decides what reaches SharePoint.</summary>
sealed class Vfs365FileSystem(DriveEngine engine, string label, Action<string>? log, Action<string>? trace = null) : FileSystemBase
{
    const uint AttributeReadOnly = 0x1, AttributeDirectory = 0x10, AttributeNormal = 0x80;
    const int AllocationUnit = 4096;

    sealed class Handle(FsEntry entry)
    {
        public FsEntry Entry { get; set; } = entry;
        public DirectoryReader? Reader { get; set; }
        public SafeFileHandle? Content { get; set; }
        public string? ContentPath { get; set; }
        public IContentSource? Source { get; set; }
        public string? SourceStaging { get; set; }
        public bool Wrote { get; set; }
        public Lock Gate { get; } = new();
    }

    /// <summary>Sorts "." and ".." first, then names case-insensitively. Listings and markers must agree on this order.</summary>
    static readonly Comparer<string> NameOrder = Comparer<string>.Create((a, b) =>
    {
        static int Rank(string name) => name == "." ? 0 : name == ".." ? 1 : 2;
        var rank = Rank(a).CompareTo(Rank(b));
        return rank != 0 ? rank : StringComparer.OrdinalIgnoreCase.Compare(a, b);
    });

    readonly byte[] securityDescriptor = OwnerOnlyDescriptor();


    public override int Init(object host0)
    {
        var host = (FileSystemHost)host0;
        host.SectorSize = AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        host.PostCleanupWhenModifiedOnly = true;
        host.FileSystemName = "VFS365";
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = 0x365365;
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = 1UL << 40;
        volumeInfo.FreeSize = 1UL << 40;
        volumeInfo.SetVolumeLabel(label);
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        var entry = Run(engine.GetEntryAsync(fileName, default));
        if (entry is null)
        {
            trace?.Invoke($"security {fileName}: not found");
            fileAttributes = 0;
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        fileAttributes = Attributes(entry);
        if (securityDescriptor is not null)
        {
            securityDescriptor = this.securityDescriptor;
        }
        return STATUS_SUCCESS;
    }

    public override int GetSecurity(object fileNode, object fileDesc, ref byte[] securityDescriptor)
    {
        securityDescriptor = this.securityDescriptor;
        return STATUS_SUCCESS;
    }

    public override int SetSecurity(object fileNode, object fileDesc, AccessControlSections sections, byte[] securityDescriptor) => STATUS_SUCCESS;

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes, byte[] securityDescriptor,
        ulong allocationSize, out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = null;
        normalizedName = null;
        var isDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0;
        trace?.Invoke($"create {fileName} dir={isDirectory} allocation={allocationSize}");
        var handle = new Handle(Run(engine.CreateAsync(fileName, isDirectory, default))) { Wrote = !isDirectory };
        if (handle.Entry.StagingPath is { } staging)
        {
            handle.Content = OpenStaging(staging);
            handle.ContentPath = staging;
        }
        fileDesc = handle;
        fileInfo = Info(handle);
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = null;
        normalizedName = null;
        var entry = Run(engine.GetEntryAsync(fileName, default));
        if (entry is null || entry.Blocked)
        {
            trace?.Invoke($"open {fileName}: {(entry is null ? "not found" : "blocked")}");
            fileDesc = null;
            fileInfo = default;
            return entry is null ? STATUS_OBJECT_NAME_NOT_FOUND : STATUS_ACCESS_DENIED;
        }
        trace?.Invoke($"open {fileName}");
        var handle = new Handle(entry);
        fileDesc = handle;
        fileInfo = Info(handle);
        return STATUS_SUCCESS;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes, ulong allocationSize, out FileInfo fileInfo)
    {
        var handle = (Handle)fileDesc;
        trace?.Invoke($"overwrite {handle.Entry.Path} allocation={allocationSize}");
        Stage(handle, truncate: true);
        fileInfo = Info(handle);
        return STATUS_SUCCESS;
    }

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var handle = (Handle)fileDesc;
        trace?.Invoke($"cleanup {handle.Entry.Path} flags=0x{flags:X} wrote={handle.Wrote}");
        try
        {
            if ((flags & CleanupDelete) != 0)
            {
                Run(engine.DeleteAsync(handle.Entry.Path, default));
            }
            else if (handle.Wrote)
            {
                Run(engine.CommitAsync(handle.Entry.Path, default));
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"error: closing {handle.Entry.Path}: {e.Message}");
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var handle = (Handle)fileDesc;
        handle.Content?.Dispose();
        handle.Source?.Dispose();
        handle.Reader?.Dispose();
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = Info((Handle)fileDesc);
        return STATUS_SUCCESS;
    }

    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        var handle = (Handle)fileDesc;
        trace?.Invoke($"read {handle.Entry.Path} offset={offset} length={length}");
        bytesTransferred = 0;
        var source = Readable(handle);
        if ((long)offset >= source.Length)
        {
            return STATUS_END_OF_FILE;
        }
        var bytes = ArrayPool<byte>.Shared.Rent((int)length);
        try
        {
            var read = Run(source.ReadAsync((long)offset, bytes.AsMemory(0, (int)length), default).AsTask());
            bytes.AsSpan(0, read).CopyTo(new Span<byte>((void*)buffer, read));
            bytesTransferred = (uint)read;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
        return STATUS_SUCCESS;
    }

    public override unsafe int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FileInfo fileInfo)
    {
        var handle = (Handle)fileDesc;
        trace?.Invoke($"write {handle.Entry.Path} offset={offset} length={length} eof={writeToEndOfFile} constrained={constrainedIo}");
        var content = Writable(handle);
        var size = RandomAccess.GetLength(content);
        if (constrainedIo)
        {
            if ((long)offset >= size)
            {
                bytesTransferred = 0;
                fileInfo = Info(handle);
                return STATUS_SUCCESS;
            }
            length = (uint)Math.Min(length, size - (long)offset);
        }
        var position = writeToEndOfFile ? size : (long)offset;
        RandomAccess.Write(content, new ReadOnlySpan<byte>((void*)buffer, (int)length), position);
        engine.MarkDirty(handle.Entry);
        if (constrainedIo)
        {
            // Paging I/O: no cleanup may follow
            engine.CommitLater(handle.Entry);
        }
        Run(engine.WrittenAsync(handle.Entry, position, length, default));
        bytesTransferred = length;
        fileInfo = Info(handle);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo)
    {
        var handle = (Handle)fileDesc;
        trace?.Invoke($"setsize {handle.Entry.Path} size={newSize} allocation={setAllocationSize}");
        var current = handle.Content is null ? handle.Entry.Size : RandomAccess.GetLength(handle.Content);
        if (!setAllocationSize || (long)newSize < current)
        {
            RandomAccess.SetLength(Writable(handle), (long)newSize);
            engine.MarkDirty(handle.Entry);
            if (!setAllocationSize)
            {
                engine.SizeSet(handle.Entry, (long)newSize);
            }
        }
        fileInfo = Info(handle);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes, ulong creationTime, ulong lastAccessTime,
        ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo)
    {
        fileInfo = Info((Handle)fileDesc);
        return STATUS_SUCCESS;
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = fileDesc is Handle handle ? Info(handle) : default;
        return STATUS_SUCCESS;
    }

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        Run(engine.CanDeleteAsync(((Handle)fileDesc).Entry, default));
        return STATUS_SUCCESS;
    }

    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        var handle = (Handle)fileDesc;
        Run(engine.RenameAsync(fileName, newFileName, replaceIfExists, default));
        handle.Entry = Run(engine.GetEntryAsync(newFileName, default)) ?? handle.Entry;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Answers with the entries that are here, waiting only when there are none yet, and marks the end only when the listing is
    /// complete. WinFsp passes a partial answer on to the caller and asks again after its last name, so a folder still loading page
    /// by page shows its first page at once.
    /// </summary>
    public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker, IntPtr buffer, uint length, out uint bytesTransferred)
    {
        var handle = (Handle)fileDesc;
        DirectoryReader reader;
        lock (handle.Gate)
        {
            reader = handle.Reader ??= new DirectoryReader(engine.ListStreamAsync(handle.Entry, default).GetAsyncEnumerator(), handle.Entry, handle.Entry.Path != "\\");
        }

        uint transferred = 0;
        var start = reader.After(marker);
        for (var index = start; ; index++)
        {
            var (found, ended) = index == start ? reader.Wait(index) : reader.Peek(index);
            if (found is var (name, entry))
            {
                var info = entry is null ? new FileInfo { FileAttributes = AttributeDirectory } : Info(entry, entry.Size);
                if (!WinFspNative.AddEntry(name, info, buffer, length, ref transferred))
                {
                    break;
                }
                continue;
            }
            if (ended)
            {
                WinFspNative.AddEnd(buffer, length, ref transferred);
            }
            break;
        }
        bytesTransferred = transferred;
        trace?.Invoke($"readdir {handle.Entry.Path} marker={marker ?? "-"} start={start} bytes={transferred}");
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// One open directory's entries in a fixed order as they arrive, so WinFsp's marker continues where the last answer ended.
    /// A cached folder is one sorted batch; a folder still loading answers each Graph page as soon as it is in.
    /// </summary>
    sealed class DirectoryReader : IDisposable
    {
        readonly IAsyncEnumerator<IReadOnlyList<FsEntry>> batches;
        readonly List<(string Name, FsEntry? Entry)> entries = [];
        readonly Dictionary<string, int> positions = new(StringComparer.OrdinalIgnoreCase);
        readonly Lock gate = new();
        bool done;

        public DirectoryReader(IAsyncEnumerator<IReadOnlyList<FsEntry>> batches, FsEntry self, bool dots)
        {
            this.batches = batches;
            if (dots)
            {
                Add(".", self);
                Add("..", null);
            }
        }

        Task<bool>? moving;

        /// <summary>Position after the entry WinFsp last returned; 0 for a new scan.</summary>
        public int After(string? marker)
        {
            if (marker is null)
            {
                return 0;
            }
            lock (gate)
            {
                while (!positions.ContainsKey(marker) && !done)
                {
                    Take(wait: true);
                }
                return positions.TryGetValue(marker, out var position) ? position + 1 : entries.Count;
            }
        }

        /// <summary>The entry at <paramref name="index"/>, waiting for the next batch when it isn't here yet. Ended past the last one.</summary>
        public ((string Name, FsEntry? Entry)? Found, bool Ended) Wait(int index)
        {
            lock (gate)
            {
                while (index >= entries.Count && !done)
                {
                    Take(wait: true);
                }
                return index < entries.Count ? (entries[index], false) : (null, true);
            }
        }

        /// <summary>Like Wait, but without waiting: neither found nor ended means the next batch isn't here yet.</summary>
        public ((string Name, FsEntry? Entry)? Found, bool Ended) Peek(int index)
        {
            lock (gate)
            {
                if (index >= entries.Count && !done)
                {
                    Take(wait: false);
                }
                return index < entries.Count ? (entries[index], false) : (null, done);
            }
        }

        /// <summary>Takes in the next batch; without waiting only when it is already here (a pending fetch is kept for later).</summary>
        void Take(bool wait)
        {
            moving ??= batches.MoveNextAsync().AsTask();
            if (!wait && !moving.IsCompleted)
            {
                return;
            }
            var more = Run(moving);
            moving = null;
            if (more)
            {
                foreach (var entry in batches.Current.OrderBy(e => e.Name, NameOrder))
                {
                    Add(entry.Name, entry);
                }
            }
            else
            {
                done = true;
            }
        }

        void Add(string name, FsEntry? entry)
        {
            if (positions.TryAdd(name, entries.Count))
            {
                entries.Add((name, entry));
            }
        }

        /// <summary>Ends the enumeration; a fetch still running is let finish first (an async iterator can't be disposed mid-step).</summary>
        public void Dispose()
        {
            lock (gate)
            {
                _ = moving is { IsCompleted: false } pending
                    ? pending.ContinueWith(_ => batches.DisposeAsync().AsTask(), TaskScheduler.Default)
                    : batches.DisposeAsync().AsTask();
            }
        }
    }

    public override int ExceptionHandler(Exception ex)
    {
        if (ex is not FsException)
        {
            log?.Invoke($"error: {ex.GetType().Name}: {ex.Message}");
        }
        return ex switch
        {
            FsException { Error: FsError.NotFound } => STATUS_OBJECT_NAME_NOT_FOUND,
            FsException { Error: FsError.NameCollision } => STATUS_OBJECT_NAME_COLLISION,
            FsException { Error: FsError.NotSameDevice } => STATUS_NOT_SAME_DEVICE,
            FsException { Error: FsError.AccessDenied } => STATUS_ACCESS_DENIED,
            FsException { Error: FsError.DirectoryNotEmpty } => STATUS_DIRECTORY_NOT_EMPTY,
            FsException { Error: FsError.ReadOnly } => STATUS_MEDIA_WRITE_PROTECTED,
            RemoteException { Error: RemoteError.NotFound } => STATUS_OBJECT_NAME_NOT_FOUND,
            RemoteException { Error: RemoteError.AccessDenied } => STATUS_ACCESS_DENIED,
            RemoteException { Error: RemoteError.Locked or RemoteError.Conflict } => STATUS_SHARING_VIOLATION,
            RemoteException { Error: RemoteError.ReadOnly } => STATUS_MEDIA_WRITE_PROTECTED,
            RemoteException { Error: RemoteError.Throttled or RemoteError.Unavailable } => STATUS_UNEXPECTED_NETWORK_ERROR,
            HttpRequestException => STATUS_NETWORK_UNREACHABLE,
            TaskCanceledException or TimeoutException => STATUS_IO_TIMEOUT,
            _ => STATUS_UNEXPECTED_IO_ERROR,
        };
    }

    /// <summary>The handle's current content: the staging file while the file is being changed, else the cached or streaming download.</summary>
    IContentSource Readable(Handle handle)
    {
        var staging = engine.StagingPathOf(handle.Entry);
        lock (handle.Gate)
        {
            if (handle.Source is not null && handle.SourceStaging == staging)
            {
                return handle.Source;
            }
        }
        var source = Run(engine.OpenContentAsync(handle.Entry, default));
        lock (handle.Gate)
        {
            handle.Source?.Dispose();
            handle.Source = source;
            handle.SourceStaging = staging;
            return source;
        }
    }

    SafeFileHandle Writable(Handle handle)
    {
        lock (handle.Gate)
        {
            if (handle.Wrote && handle.Content is not null && handle.ContentPath == engine.StagingPathOf(handle.Entry))
            {
                return handle.Content;
            }
        }
        return Stage(handle, truncate: false);
    }

    SafeFileHandle Stage(Handle handle, bool truncate)
    {
        var staging = Run(engine.OpenForWriteAsync(handle.Entry, truncate, default));
        lock (handle.Gate)
        {
            if (handle.ContentPath != staging || handle.Content is null)
            {
                handle.Content?.Dispose();
                handle.Content = OpenStaging(staging);
                handle.ContentPath = staging;
            }
            handle.Wrote = true;
            return handle.Content;
        }
    }

    static SafeFileHandle OpenStaging(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);

    static T Run<T>(Task<T> task) => task.GetAwaiter().GetResult();

    static void Run(Task task) => task.GetAwaiter().GetResult();

    static uint Attributes(FsEntry entry) =>
        entry.IsDirectory ? AttributeDirectory : entry.ReadOnly ? AttributeReadOnly : AttributeNormal;

    static FileInfo Info(Handle handle)
    {
        long size;
        lock (handle.Gate)
        {
            size = handle.Content is { IsClosed: false } content ? RandomAccess.GetLength(content) : handle.Entry.Size;
        }
        return Info(handle.Entry, size);
    }

    static FileInfo Info(FsEntry entry, long size) => new()
    {
        FileAttributes = Attributes(entry),
        FileSize = (ulong)size,
        AllocationSize = ((ulong)size + AllocationUnit - 1) / AllocationUnit * AllocationUnit,
        CreationTime = (ulong)entry.Created.UtcDateTime.ToFileTimeUtc(),
        LastAccessTime = (ulong)entry.Modified.UtcDateTime.ToFileTimeUtc(),
        LastWriteTime = (ulong)entry.Modified.UtcDateTime.ToFileTimeUtc(),
        ChangeTime = (ulong)entry.Modified.UtcDateTime.ToFileTimeUtc(),
        IndexNumber = Fnv1a(entry.ItemId ?? entry.Path),
    };

    /// <summary>Stable 64-bit file ID from the item ID.</summary>
    static ulong Fnv1a(string text)
    {
        var hash = 14695981039346656037UL;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 1099511628211UL;
        }
        return hash;
    }

    /// <summary>Full control for the mounting user and SYSTEM only.</summary>
    static byte[] OwnerOnlyDescriptor()
    {
        var user = WindowsIdentity.GetCurrent().User!.Value;
        var descriptor = new RawSecurityDescriptor($"O:{user}G:{user}D:P(A;;FA;;;{user})(A;;FA;;;SY)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }
}
