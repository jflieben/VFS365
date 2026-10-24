using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Fsp;
using Microsoft.Win32.SafeHandles;
using Vfs365.Core;
using Vfs365.Core.Drive;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Vfs365.Drive.WinFsp;

/// <summary>
/// WinFsp callbacks over the engine. Content lives in local files (downloaded or staged); the engine decides what reaches SharePoint.
/// A handle keeps the entry it was given and follows it (<see cref="DriveEngine.Follow"/>), so it reads and writes the content it saw.
/// </summary>
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

        /// <summary>The staging file or item version Source reads.</summary>
        public string? SourceVersion { get; set; }

        /// <summary>Sources replaced by another version while a read may still use them; disposed with the handle.</summary>
        public List<IContentSource> Retired { get; } = [];
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
        if (!FromOwner(fileName))
        {
            fileAttributes = 0;
            return STATUS_ACCESS_DENIED;
        }
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
        if (!FromOwner(fileName))
        {
            fileDesc = null;
            fileInfo = default;
            return STATUS_ACCESS_DENIED;
        }
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
        if (!FromOwner(fileName))
        {
            fileDesc = null;
            fileInfo = default;
            return STATUS_ACCESS_DENIED;
        }
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
        handle.Entry = engine.Follow(handle.Entry);
        Stage(handle, truncate: true);
        fileInfo = Info(handle);
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Acts on the file's name now, not the one it was opened with: another handle may have renamed it since. WinFsp sends that name
    /// with a delete; a commit takes it from the content the handle follows.
    /// </summary>
    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var handle = (Handle)fileDesc;
        var path = fileName ?? handle.Entry.Path;
        trace?.Invoke($"cleanup {path} flags=0x{flags:X} wrote={handle.Wrote}");
        try
        {
            if ((flags & CleanupDelete) != 0)
            {
                Run(engine.DeleteAsync(path, default));
            }
            else if (handle.Wrote)
            {
                path = (handle.Entry = engine.Follow(handle.Entry)).Path;
                Run(engine.CommitAsync(path, default));
            }
        }
        catch (FsException e) when (e.Error == FsError.Invalid)
        {
            // Its content was deleted or replaced meanwhile: nothing to commit
        }
        catch (Exception e)
        {
            log?.Invoke($"error: closing {path}: {e.Message}");
        }
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var handle = (Handle)fileDesc;
        handle.Content?.Dispose();
        handle.Source?.Dispose();
        foreach (var retired in handle.Retired)
        {
            retired.Dispose();
        }
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
        catch (Exception e) when (e is not FsException)
        {
            if (e is not ContentChangedException) // the engine logged it, with the sizes
            {
                log?.Invoke($"error: reading {handle.Entry.Path}: {e.GetType().Name}: {e.Message}");
            }
            return Status(e);
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
        var (content, entry, lease) = BeginWrite(handle);
        long position;
        using (lease)
        {
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
            position = writeToEndOfFile ? size : (long)offset;
            RandomAccess.Write(content, new ReadOnlySpan<byte>((void*)buffer, (int)length), position);
            engine.MarkDirty(entry);
        }
        if (constrainedIo)
        {
            // Paging I/O: no cleanup may follow
            engine.CommitLater(entry);
        }
        Run(engine.WrittenAsync(entry, position, length, default));
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
            if (newSize == 0)
            {
                // Cut to nothing: stage empty instead of downloading what goes
                handle.Entry = engine.Follow(handle.Entry);
                bool staged;
                lock (handle.Gate)
                {
                    staged = handle.Wrote && handle.Content is not null && handle.ContentPath == handle.Entry.StagingPath;
                }
                if (!staged)
                {
                    Stage(handle, truncate: true);
                }
            }
            var (content, entry, lease) = BeginWrite(handle);
            using (lease)
            {
                RandomAccess.SetLength(content, (long)newSize);
                engine.MarkDirty(entry);
            }
            if (!setAllocationSize)
            {
                engine.SizeSet(entry, (long)newSize);
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

    /// <summary>Forces what the handle wrote to disk (its staging file). A volume flush (no handle) has nothing to do.</summary>
    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        if (fileDesc is not Handle handle)
        {
            fileInfo = default;
            return STATUS_SUCCESS;
        }
        lock (handle.Gate)
        {
            if (handle.Content is { IsClosed: false } content && !FlushFileBuffers(content))
            {
                fileInfo = default;
                return NtStatusFromWin32((uint)Marshal.GetLastPInvokeError());
            }
        }
        fileInfo = Info(handle);
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
            if (marker is null && handle.Reader is { } earlier)
            {
                // A new scan on the same handle (RestartScan): the folder is read again, not served from the first scan
                earlier.Dispose();
                handle.Reader = null;
            }
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
        if (ex is not (FsException or ContentChangedException))
        {
            log?.Invoke($"error: {ex.GetType().Name}: {ex.Message}");
        }
        return Status(ex);
    }

    static int Status(Exception ex) =>
        ex switch
        {
            FsException { Error: FsError.NotFound } => STATUS_OBJECT_NAME_NOT_FOUND,
            FsException { Error: FsError.NameCollision } => STATUS_OBJECT_NAME_COLLISION,
            FsException { Error: FsError.NotSameDevice } => STATUS_NOT_SAME_DEVICE,
            FsException { Error: FsError.AccessDenied } => STATUS_ACCESS_DENIED,
            FsException { Error: FsError.DirectoryNotEmpty } => STATUS_DIRECTORY_NOT_EMPTY,
            FsException { Error: FsError.ReadOnly } => STATUS_MEDIA_WRITE_PROTECTED,
            FsException { Error: FsError.Invalid } => STATUS_FILE_INVALID,
            RemoteException { Error: RemoteError.NotFound } => STATUS_OBJECT_NAME_NOT_FOUND,
            RemoteException { Error: RemoteError.AccessDenied } => STATUS_ACCESS_DENIED,
            // 409 (a folder created or moved onto a taken name) is a name collision; 412 and 423 (changed or locked) a sharing violation
            RemoteException { Error: RemoteError.Conflict } taken when taken.Message.StartsWith("409", StringComparison.Ordinal) => STATUS_OBJECT_NAME_COLLISION,
            RemoteException { Error: RemoteError.Locked or RemoteError.Conflict } => STATUS_SHARING_VIOLATION,
            RemoteException { Error: RemoteError.ReadOnly } => STATUS_MEDIA_WRITE_PROTECTED,
            RemoteException { Error: RemoteError.Rejected } => STATUS_OBJECT_NAME_INVALID, // a name or request SharePoint refuses
            RemoteException { Error: RemoteError.Throttled or RemoteError.Unavailable } => STATUS_UNEXPECTED_NETWORK_ERROR,
            ContentChangedException => STATUS_FILE_INVALID, // "externally altered so that the opened file is no longer valid"
            HttpRequestException => STATUS_NETWORK_UNREACHABLE,
            TaskCanceledException or TimeoutException => STATUS_IO_TIMEOUT,
            _ => STATUS_UNEXPECTED_IO_ERROR,
        };

    /// <summary>
    /// The handle's current content: the staging file while the file is being changed, else the cached or streaming download of the
    /// version it follows. A file deleted while open keeps serving what the handle already had.
    /// </summary>
    IContentSource Readable(Handle handle)
    {
        FsEntry entry;
        try
        {
            entry = handle.Entry = engine.Follow(handle.Entry);
        }
        catch (FsException)
        {
            lock (handle.Gate)
            {
                if (handle.Source is { } kept)
                {
                    return kept;
                }
            }
            throw;
        }
        var version = VersionOf(entry);
        lock (handle.Gate)
        {
            if (handle.Source is not null && handle.SourceVersion == version)
            {
                return handle.Source;
            }
        }
        var source = Run(engine.OpenContentAsync(entry, default));
        lock (handle.Gate)
        {
            if (handle.Source is not null && handle.SourceVersion == version)
            {
                source.Dispose(); // another read on this handle opened it meanwhile
                return handle.Source;
            }
            if (handle.Source is { } replaced)
            {
                handle.Retired.Add(replaced); // a read on another thread may still use it
            }
            handle.Source = source;
            handle.SourceVersion = version;
            return source;
        }
    }

    static string VersionOf(FsEntry entry) => entry.StagingPath ?? $"{entry.ItemId}|{entry.ContentTag}";

    /// <summary>
    /// The staging file a write goes to, with the engine's write lease taken: dispose it after the write and MarkDirty. A handle whose
    /// content was uploaded stages again from the item it went into (a conflict copy included), never from a version it didn't write;
    /// once its content was deleted or replaced, writes fail (STATUS_FILE_INVALID).
    /// </summary>
    (SafeFileHandle Content, FsEntry Entry, IDisposable Lease) BeginWrite(Handle handle)
    {
        while (true)
        {
            var entry = handle.Entry = engine.Follow(handle.Entry);
            lock (handle.Gate)
            {
                if (handle.Wrote && handle.Content is { } content && handle.ContentPath == entry.StagingPath && engine.BeginWrite(entry) is { } lease)
                {
                    return (content, entry, lease);
                }
            }
            Stage(handle, truncate: false);
        }
    }

    SafeFileHandle Stage(Handle handle, bool truncate)
    {
        var staged = Run(engine.StageAsync(handle.Entry, truncate, default));
        lock (handle.Gate)
        {
            handle.Entry = staged;
            if (handle.ContentPath != staged.StagingPath || handle.Content is null)
            {
                handle.Content?.Dispose();
                handle.Content = OpenStaging(staged.StagingPath!);
                handle.ContentPath = staged.StagingPath;
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

    /// <summary>The handle's size is what its reads serve: the staging file, else the opened content of its version, else the entry's.</summary>
    static FileInfo Info(Handle handle)
    {
        long size;
        lock (handle.Gate)
        {
            size = handle.Content is { IsClosed: false } content ? RandomAccess.GetLength(content)
                : handle.Source is { } source && handle.SourceVersion == VersionOf(handle.Entry) ? source.Length
                : handle.Entry.Size;
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

    static readonly SecurityIdentifier Owner = WindowsIdentity.GetCurrent().User!;
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    int tokenUnreadable, checkFailed;

    /// <summary>
    /// False when the open being served comes from someone other than the volume's user or SYSTEM (elevated processes of the user have
    /// the same SID), or from a token of theirs that the volume's descriptor gives no access at all: an app container, or a restricted
    /// token whose restricting SIDs don't include them (sandboxed browser and document renderers). Checked before the engine sees the
    /// name, so such an open costs no lookup and can't probe names; WinFsp's access check against the volume's descriptor follows anyway.
    /// Low integrity tokens still pass for reading, as on a local disk (Explorer's preview handlers run at low integrity); WinFsp's check
    /// refuses their writes. Reads the requester's token, which WinFsp passes with each create for that check: it covers impersonating
    /// callers and needs no rights on another user's process. Undecidable cases are allowed (logged once).
    /// </summary>
    bool FromOwner(string fileName)
    {
        Requester.Verdict verdict;
        string? problem = null;
        try
        {
            verdict = Requester.Check(securityDescriptor);
        }
        catch (Exception e)
        {
            (verdict, problem) = (new Requester.Verdict(null, null, 0), e.Message);
        }
        if (verdict.User is not { } user)
        {
            if (Interlocked.Exchange(ref tokenUnreadable, 1) == 0)
            {
                log?.Invoke($"error: the requester of an open could not be read, opens are left to WinFsp's own access check{(problem is null ? "" : $": {problem}")}");
            }
            return true;
        }
        if (user != Owner && user != LocalSystem)
        {
            trace?.Invoke($"refused {fileName}: requested by {user.Value}");
            return false;
        }
        if (verdict.Granted == false)
        {
            trace?.Invoke($"refused {fileName}: a restricted or app container token of the user");
            return false;
        }
        if (verdict.Granted is null && Interlocked.Exchange(ref checkFailed, 1) == 0)
        {
            log?.Invoke($"error: the access check on an open could not run (error {verdict.Error}), such opens are left to WinFsp's own access check");
        }
        return true;
    }

    /// <summary>The requester of the create request being served, from WinFsp's request (FSP_FSCTL_TRANSACT_REQ).</summary>
    static unsafe class Requester
    {
        const uint CreateKind = 1; // FspFsctlTransactCreateKind
        const int KindOffset = 4, AccessTokenOffset = 40, TokenUser = 1;
        const uint FileReadAttributes = 0x80;

        /// <summary>
        /// The requester's user (null: unreadable) and whether its token gets any access under the volume's descriptor (null: the check
        /// couldn't run, Error says why).
        /// </summary>
        public readonly record struct Verdict(SecurityIdentifier? User, bool? Granted, int Error);

        /// <summary>FILE_GENERIC_READ, FILE_GENERIC_WRITE, FILE_GENERIC_EXECUTE, FILE_ALL_ACCESS.</summary>
        static GenericMapping fileMapping = new(0x120089, 0x120116, 0x1200A0, 0x1F01FF);

        static readonly delegate* unmanaged<IntPtr> OperationContext = (delegate* unmanaged<IntPtr>)NativeLibrary.GetExport(
            GetModuleHandle(RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "winfsp-a64.dll",
                Architecture.X86 => "winfsp-x86.dll",
                _ => "winfsp-x64.dll",
            }), "FspFileSystemGetOperationContext");

        /// <summary>
        /// Only creates carry a token. Other requests act on handles that passed this check, and creates without a token come from
        /// kernel mode; both count as the owner's. The access check asks for FILE_READ_ATTRIBUTES, the least any open is given: the
        /// descriptor grants all or nothing per SID, so a token refused that gets nothing from WinFsp's own check either.
        /// </summary>
        public static Verdict Check(byte[] descriptor)
        {
            var context = (byte**)OperationContext();
            if (context is null || *context is null)
            {
                return new(null, null, 0);
            }
            var request = *context;
            var token = (IntPtr)(long)(*(ulong*)(request + AccessTokenOffset) & 0xffffffff); // low half: a handle in this process
            if (*(uint*)(request + KindOffset) != CreateKind || token == IntPtr.Zero)
            {
                return new(Owner, true, 0);
            }
            var info = stackalloc byte[256];
            if (!GetTokenInformation(token, TokenUser, info, 256, out _))
            {
                return new(null, null, Marshal.GetLastPInvokeError());
            }
            var user = new SecurityIdentifier(*(IntPtr*)info);
            var privileges = stackalloc byte[256];
            var privilegesLength = 256;
            fixed (byte* sd = descriptor)
            {
                return AccessCheck(sd, token, FileReadAttributes, ref fileMapping, privileges, ref privilegesLength, out _, out var granted)
                    ? new(user, granted, 0)
                    : new(user, null, Marshal.GetLastPInvokeError());
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct GenericMapping(uint read, uint write, uint execute, uint all)
        {
            public uint Read = read, Write = write, Execute = execute, All = all;
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int infoClass, byte* info, int length, out int returned);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool AccessCheck(byte* securityDescriptor, IntPtr clientToken, uint desiredAccess, ref GenericMapping genericMapping,
            byte* privilegeSet, ref int privilegeSetLength, out uint grantedAccess, out bool accessStatus);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr GetModuleHandle(string moduleName);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool FlushFileBuffers(SafeFileHandle file);
}
