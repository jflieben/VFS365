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
/// Reads, writes and directory reads that have to wait answer later (STATUS_PENDING), so a slow download holds no dispatcher thread.
/// With <paramref name="winFspGuard"/> false, WinFsp's namespace lock is replaced by none (<see cref="WinFspNative.ReplaceOperationGuard"/>).
/// </summary>
sealed class Vfs365FileSystem(DriveEngine engine, string label, Action<string>? log, Action<string>? trace = null, bool winFspGuard = false) : FileSystemBase
{
    const uint AttributeReadOnly = 0x1, AttributeDirectory = 0x10, AttributeNormal = 0x80;
    const int AllocationUnit = 4096;
    const uint FileSupersede = 0, FileOpenIf = 3, FileOverwriteIf = 5;

    sealed class Handle(FsEntry entry)
    {
        public FsEntry Entry { get; set; } = entry;
        public DirectoryReader? Reader { get; set; }
        public SafeFileHandle? Content { get; set; }
        public string? ContentPath { get; set; }
        public IContentSource? Source { get; set; }

        /// <summary>The staging file or item version Source reads.</summary>
        public string? SourceVersion { get; set; }

        /// <summary>Sources replaced by another version while a read may still use them; disposed when no read is running.</summary>
        public List<IContentSource> Retired { get; } = [];

        /// <summary>Reads running on this handle (under Gate).</summary>
        public int Reading { get; set; }
        public bool Wrote { get; set; }
        public Lock Gate { get; } = new();

        /// <summary>For the handle that created its file: its access and sharing, which opens of the file are checked against.</summary>
        public Sharing? Created { get; init; }

        /// <summary>The process that opened it: renames and deletes through it are that app's (<see cref="DriveEngine.Opened"/>).</summary>
        public uint Opener { get; init; }

        /// <summary>The version of its item it opened, known to the engine until its cleanup or close.</summary>
        public IDisposable? Version { get; init; }
    }

    /// <summary>
    /// The access and sharing of a handle that created a file, until its cleanup or close. WinFsp checks sharing only once the create
    /// is answered, and without the namespace lock another open can find the file before that: it then gets in first, or even ends
    /// before the creator's check, which then passes on content the creator thinks is new and empty. So opens of a file whose creator
    /// still holds it are checked here, with NTFS's rules (IoCheckShareAccess), as WinFsp would have if the creator had been first.
    /// </summary>
    sealed class Sharing(uint access, uint share)
    {
        const uint ReadData = 0x1, WriteData = 0x2, AppendData = 0x4, Execute = 0x20, Delete = 0x10000;
        const uint ShareRead = 0x1, ShareWrite = 0x2, ShareDelete = 0x4;

        volatile bool holding = true;

        public void Release() => holding = false;

        /// <summary>True when an open with this access and sharing must be refused while the creator holds the file.</summary>
        public bool Refuses(uint openAccess, uint openShare)
        {
            if (!holding || !UsesData(access) || !UsesData(openAccess))
            {
                return false;
            }
            return Conflicts(openAccess, share) || Conflicts(access, openShare);
        }

        static bool UsesData(uint access) => (access & (ReadData | WriteData | AppendData | Execute | Delete)) != 0;

        /// <summary>The access asks for something the other side doesn't share.</summary>
        static bool Conflicts(uint access, uint shared) =>
            (access & (ReadData | Execute)) != 0 && (shared & ShareRead) == 0
            || (access & (WriteData | AppendData)) != 0 && (shared & ShareWrite) == 0
            || (access & Delete) != 0 && (shared & ShareDelete) == 0;
    }

    /// <summary>An open of <paramref name="entry"/> its creator's sharing refuses (see <see cref="Sharing"/>).</summary>
    static bool RefusedByCreator(FsEntry entry, uint grantedAccess) =>
        entry.Creator is Sharing creator && Requester.ShareAccess() is { } share && creator.Refuses(grantedAccess, share);

    FileSystemHost? host;

    readonly PendingAnswers pending = new(Status, log);

    /// <summary>True when WinFsp's namespace lock was replaced at the mount.</summary>
    public bool GuardReplaced { get; private set; }

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
        var host = this.host = (FileSystemHost)host0;
        host.SectorSize = AllocationUnit;
        host.SectorsPerAllocationUnit = 1;
        host.MaxComponentLength = 255;
        host.FileInfoTimeout = 1000; // also the volume info and security timeouts, which aren't set apart
        // No directory cache: WinFsp would serve a scan's first answer from another handle's (partial ones too) and continue it on
        // this handle's reader, whose order can differ, so names were skipped or repeated. Listings are in memory anyway.
        host.DirInfoTimeout = 0;
        host.CaseSensitiveSearch = false;
        host.CasePreservedNames = true;
        host.UnicodeOnDisk = true;
        host.PersistentAcls = false;
        // Every close: WinFsp counts a new file closed without writes (Explorer's New > Text Document, a download's placeholder) as
        // unmodified, and without its Cleanup the file would upload only at unmount
        host.PostCleanupWhenModifiedOnly = false;
        host.PassQueryDirectoryFileName = true; // a search for one exact name is a lookup (GetDirInfoByName), not a scan
        host.FileSystemName = "VFS365";
        host.VolumeCreationTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        host.VolumeSerialNumber = 0x365365;
        return STATUS_SUCCESS;
    }

    /// <summary>After the mount point is set, before WinFsp's dispatcher starts: the moment to change the operation guard.</summary>
    public override int Mounted(object host0)
    {
        if (!winFspGuard)
        {
            var kept = WinFspNative.ReplaceOperationGuard((FileSystemHost)host0);
            GuardReplaced = kept is null;
            if (kept is not null)
            {
                log?.Invoke($"WinFsp's namespace lock stays on: {kept}");
            }
        }
        return STATUS_SUCCESS;
    }

    /// <summary>After WinFsp's dispatcher stopped (pending requests are cancelled then), before the native file system is freed.</summary>
    public override void Unmounted(object host0) => pending.Stop();

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
        var opener = Requester.ProcessId();
        trace?.Invoke($"create {fileName} dir={isDirectory} allocation={allocationSize} access=0x{grantedAccess:X} share=0x{Requester.ShareAccess():X} pid={opener}");
        Handle handle;
        try
        {
            var sharing = !isDirectory && Requester.ShareAccess() is { } share ? new Sharing(grantedAccess, share) : null;
            handle = new Handle(Run(engine.CreateAsync(fileName, isDirectory, default, sharing))) { Wrote = !isDirectory, Created = sharing, Opener = opener };
            if (handle.Entry.StagingPath is { } staging)
            {
                try
                {
                    handle.Content = OpenStaging(staging);
                    handle.ContentPath = staging;
                }
                catch (FileNotFoundException)
                {
                    // Deleted or replaced through another handle already (no namespace lock): writes follow it (STATUS_FILE_INVALID)
                    trace?.Invoke($"create {fileName}: gone meanwhile");
                }
            }
        }
        catch (FsException e) when (e.Error == FsError.NameCollision && (createOptions >> 24) is FileOpenIf or FileOverwriteIf or FileSupersede
            && Run(engine.GetEntryAsync(fileName, default)) is { Blocked: false } existing && existing.IsDirectory == isDirectory)
        {
            // Created by another open-or-create since WinFsp found it missing (no namespace lock serializes them): opened, and WinFsp's
            // share check decides. An overwrite can't truncate here, before that check: one of an empty file changes nothing, one of
            // a file its creator is writing (most likely still open) is refused as NTFS would then, as is one its creator's sharing refuses.
            if ((createOptions >> 24) != FileOpenIf && existing.Size > 0 || RefusedByCreator(existing, grantedAccess))
            {
                trace?.Invoke($"create {fileName}: created meanwhile, refused");
                fileDesc = null;
                fileInfo = default;
                return STATUS_SHARING_VIOLATION;
            }
            trace?.Invoke($"create {fileName}: created meanwhile, opened");
            handle = new Handle(existing) { Opener = opener, Version = engine.Opened(existing, opener) };
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
        if (entry is { StagingPath: { } staging } && !File.Exists(staging))
        {
            // Staged content deleted or replaced while it was looked up (no namespace lock orders deletes and opens): what is there now
            entry = Run(engine.GetEntryAsync(fileName, default));
        }
        if (entry is null || entry.Blocked)
        {
            trace?.Invoke($"open {fileName}: {(entry is null ? "not found" : "blocked")}");
            fileDesc = null;
            fileInfo = default;
            return entry is null ? STATUS_OBJECT_NAME_NOT_FOUND : STATUS_ACCESS_DENIED;
        }
        if (RefusedByCreator(entry, grantedAccess))
        {
            trace?.Invoke($"open {fileName}: refused by its creator's sharing");
            fileDesc = null;
            fileInfo = default;
            return STATUS_SHARING_VIOLATION;
        }
        var opener = Requester.ProcessId();
        trace?.Invoke($"open {fileName} pid={opener}");
        var handle = new Handle(entry) { Opener = opener, Version = engine.Opened(entry, opener) };
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
    /// with a delete; a commit takes it from the content the handle follows. The version the handle opened counts until after that.
    /// </summary>
    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var handle = (Handle)fileDesc;
        handle.Created?.Release();
        var path = fileName ?? handle.Entry.Path;
        trace?.Invoke($"cleanup {path} flags=0x{flags:X} wrote={handle.Wrote}");
        try
        {
            if ((flags & CleanupDelete) != 0)
            {
                Run(engine.DeleteAsync(path, default, handle.Opener));
            }
            else if (handle.Wrote)
            {
                Run(engine.CommitAsync(handle.Entry, default)); // only the content this handle wrote
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
        handle.Version?.Dispose();
    }

    public override void Close(object fileNode, object fileDesc)
    {
        var handle = (Handle)fileDesc;
        handle.Created?.Release(); // a create WinFsp refused after all (sharing) gets no cleanup
        handle.Version?.Dispose();
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

    /// <summary>
    /// Answers at once when the bytes are here (cached, staged, already downloaded); otherwise when they arrive. WinFsp's buffer is
    /// written only in this callback, or right before the pending answer while the volume is mounted (the unmount cancels pending
    /// requests and unmaps their buffers), see <see cref="PendingAnswers"/>.
    /// </summary>
    public override int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred)
    {
        var handle = (Handle)fileDesc;
        trace?.Invoke($"read {handle.Entry.Path} offset={offset} length={length}");
        var bytes = ArrayPool<byte>.Shared.Rent((int)length);
        var reading = ReadAsync(handle, (long)offset, bytes.AsMemory(0, (int)length));
        if (reading.IsCompleted)
        {
            int status;
            (status, bytesTransferred) = reading.Result;
            Marshal.Copy(bytes, 0, buffer, (int)bytesTransferred);
            ArrayPool<byte>.Shared.Return(bytes);
            return status;
        }
        trace?.Invoke($"read {handle.Entry.Path}: pending");
        var hint = host!.GetOperationRequestHint();
        _ = AnswerReadAsync(reading, bytes, buffer, hint, handle.Entry.Path);
        bytesTransferred = 0;
        return STATUS_PENDING;
    }

    async Task AnswerReadAsync(ValueTask<(int Status, uint Bytes)> reading, byte[] bytes, IntPtr buffer, ulong hint, string path)
    {
        await pending.AnswerAsync($"read {path}", reading, read =>
        {
            Marshal.Copy(bytes, 0, buffer, (int)read.Bytes);
            return read;
        }, (status, read) => host!.SendReadResponse(hint, status, read));
        ArrayPool<byte>.Shared.Return(bytes);
    }

    /// <summary>Reads into <paramref name="bytes"/>. An empty file needs no content. Errors become the status to answer with.</summary>
    async ValueTask<(int Status, uint Bytes)> ReadAsync(Handle handle, long offset, Memory<byte> bytes)
    {
        IContentSource? source = null;
        try
        {
            source = await ReadableAsync(handle);
            if (source is null || offset >= source.Length)
            {
                return (STATUS_END_OF_FILE, 0);
            }
            return (STATUS_SUCCESS, (uint)await source.ReadAsync(offset, bytes, default));
        }
        catch (Exception e)
        {
            if (e is not (FsException or ContentChangedException)) // ContentChangedException: the engine logged it, with the sizes
            {
                log?.Invoke($"error: reading {handle.Entry.Path}: {e.GetType().Name}: {e.Message}");
            }
            return (Status(e), 0);
        }
        finally
        {
            if (source is not null)
            {
                DoneReading(handle);
            }
        }
    }

    /// <summary>
    /// Copies to the staging file at once; answers when the engine took the write in, which waits while a streaming upload's
    /// previous fragment is in flight (and, for a handle's first write, while the file stages: then the bytes are copied out of
    /// WinFsp's buffer first, which an unmount meanwhile unmaps).
    /// </summary>
    public override unsafe int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FileInfo fileInfo)
    {
        var handle = (Handle)fileDesc;
        trace?.Invoke($"write {handle.Entry.Path} offset={offset} length={length} eof={writeToEndOfFile} constrained={constrainedIo}");
        ValueTask<(int Status, uint Bytes)> writing;
        try
        {
            writing = TryWrite(handle, new ReadOnlySpan<byte>((void*)buffer, (int)length), (long)offset, toEnd: writeToEndOfFile, constrainedIo) is { } done
                ? WrittenAsync(handle, done, constrainedIo)
                : WriteAsync(handle, new ReadOnlySpan<byte>((void*)buffer, (int)length).ToArray(), (long)offset, writeToEndOfFile, constrainedIo);
        }
        catch (Exception e)
        {
            writing = ValueTask.FromResult(Failed(handle, e));
        }
        if (writing.IsCompleted)
        {
            (var status, bytesTransferred) = writing.Result;
            fileInfo = status == STATUS_SUCCESS ? Info(handle) : default;
            return status;
        }
        trace?.Invoke($"write {handle.Entry.Path}: pending");
        var hint = host!.GetOperationRequestHint();
        _ = AnswerWriteAsync(writing, hint, handle);
        bytesTransferred = 0;
        fileInfo = default;
        return STATUS_PENDING;
    }

    /// <summary>With the handle's file info after the write, worked out where a throw still answers (an error then).</summary>
    Task AnswerWriteAsync(ValueTask<(int Status, uint Bytes)> writing, ulong hint, Handle handle)
    {
        var info = default(FileInfo);
        return pending.AnswerAsync($"write {handle.Entry.Path}", writing, written =>
        {
            info = written.Status == STATUS_SUCCESS ? Info(handle) : default;
            return written;
        }, (status, bytes) => host!.SendWriteResponse(hint, status, bytes, ref info));
    }

    /// <summary>A write that has to stage first, with its bytes copied out of WinFsp's buffer.</summary>
    async ValueTask<(int Status, uint Bytes)> WriteAsync(Handle handle, byte[] data, long offset, bool toEnd, bool constrained)
    {
        try
        {
            while (true)
            {
                await StageAsync(handle, truncate: false);
                if (TryWrite(handle, data, offset, toEnd, constrained) is { } done)
                {
                    return await WrittenAsync(handle, done, constrained);
                }
            }
        }
        catch (Exception e)
        {
            return Failed(handle, e);
        }
    }

    /// <summary>After the bytes are in the staging file: tells the engine, which may wait for a streaming upload's fragment.</summary>
    async ValueTask<(int Status, uint Bytes)> WrittenAsync(Handle handle, (FsEntry Entry, long Position, uint Length) done, bool constrained)
    {
        try
        {
            var (entry, position, written) = done;
            if (written == 0)
            {
                return (STATUS_SUCCESS, 0);
            }
            if (constrained)
            {
                // Paging I/O: no cleanup may follow
                engine.CommitLater(entry);
            }
            await engine.WrittenAsync(entry, position, written, default);
            return (STATUS_SUCCESS, written);
        }
        catch (Exception e)
        {
            return Failed(handle, e);
        }
    }

    (int Status, uint Bytes) Failed(Handle handle, Exception e)
    {
        if (e is not FsException)
        {
            log?.Invoke($"error: writing {handle.Entry.Path}: {e.GetType().Name}: {e.Message}");
        }
        return (Status(e), 0);
    }

    /// <summary>
    /// Writes into the handle's staging file with the engine's write lease held (taken and let go on this thread); null when the
    /// handle has to stage first. Constrained (paging) I/O never extends the file.
    /// </summary>
    (FsEntry Entry, long Position, uint Length)? TryWrite(Handle handle, ReadOnlySpan<byte> data, long offset, bool toEnd, bool constrained)
    {
        if (Writable(handle) is not { } writable)
        {
            return null;
        }
        var (content, entry, lease) = writable;
        using (lease)
        {
            var size = RandomAccess.GetLength(content);
            if (constrained)
            {
                if (offset >= size)
                {
                    return (entry, offset, 0);
                }
                data = data[..(int)Math.Min(data.Length, size - offset)];
            }
            var position = toEnd ? size : offset;
            RandomAccess.Write(content, data, position);
            engine.MarkDirty(entry);
            return (entry, position, (uint)data.Length);
        }
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
        trace?.Invoke($"rename {fileName} to {newFileName} replace={replaceIfExists}");
        try
        {
            Run(engine.RenameAsync(fileName, newFileName, replaceIfExists, default, handle.Opener));
        }
        catch (Exception e) when (e is not FsException)
        {
            log?.Invoke($"error: renaming {fileName} to {newFileName}: {e.GetType().Name}: {e.Message}");
            return Status(e);
        }
        handle.Entry = Run(engine.GetEntryAsync(newFileName, default)) ?? handle.Entry;
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// Answers with the entries that are here, waiting (pending) only when there are none yet, and marks the end only when the listing
    /// is complete. WinFsp passes a partial answer on to the caller and asks again after its last name, so a folder still loading page
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
        if (reader.TryAnswer(marker, buffer, length, out bytesTransferred))
        {
            trace?.Invoke($"readdir {handle.Entry.Path} marker={marker ?? "-"} bytes={bytesTransferred}");
            return STATUS_SUCCESS;
        }
        trace?.Invoke($"readdir {handle.Entry.Path} marker={marker ?? "-"}: pending");
        var hint = host!.GetOperationRequestHint();
        _ = AnswerReadDirectoryAsync(reader, marker, buffer, length, hint, handle.Entry.Path);
        return STATUS_PENDING;
    }

    /// <summary>Fills a buffer of its own while it waits; WinFsp's is written only right before the answer (see <see cref="Read"/>).</summary>
    Task AnswerReadDirectoryAsync(DirectoryReader reader, string? marker, IntPtr buffer, uint length, ulong hint, string path)
    {
        byte[] own = [];
        return pending.AnswerAsync($"listing {path}", ListAsync(), listed =>
        {
            Marshal.Copy(own, 0, buffer, (int)listed.Bytes);
            return listed;
        }, (status, transferred) => host!.SendReadDirectoryResponse(hint, status, transferred));

        async ValueTask<(int Status, uint Bytes)> ListAsync()
        {
            try
            {
                own = GC.AllocateUninitializedArray<byte>((int)length, pinned: true);
                uint transferred;
                while (!reader.TryAnswer(marker, Marshal.UnsafeAddrOfPinnedArrayElement(own, 0), length, out transferred))
                {
                    await reader.MoreAsync();
                }
                return (STATUS_SUCCESS, transferred);
            }
            catch (Exception e)
            {
                if (e is not FsException)
                {
                    log?.Invoke($"error: listing {path}: {e.GetType().Name}: {e.Message}");
                }
                return (Status(e), 0);
            }
        }
    }

    /// <summary>One name in a directory, for a search on that exact name (FindFirstFile on a path without wildcards): a lookup.</summary>
    public override int GetDirInfoByName(object fileNode, object fileDesc, string fileName, out string? normalizedName, out FileInfo fileInfo)
    {
        var directory = ((Handle)fileDesc).Entry;
        normalizedName = null;
        fileInfo = default;
        if (fileName is "." or "..")
        {
            // As listed: not in the root
            if (directory.Path == "\\")
            {
                return STATUS_OBJECT_NAME_NOT_FOUND;
            }
            normalizedName = fileName;
            fileInfo = fileName == "." ? Info(directory, directory.Size) : new FileInfo { FileAttributes = AttributeDirectory };
            return STATUS_SUCCESS;
        }
        var entry = Run(engine.GetEntryAsync(directory.Path == "\\" ? $"\\{fileName}" : $"{directory.Path}\\{fileName}", default));
        trace?.Invoke($"dirinfo {directory.Path} {fileName}: {(entry is null ? "not found" : "found")}");
        if (entry is null)
        {
            return STATUS_OBJECT_NAME_NOT_FOUND;
        }
        normalizedName = entry.Name;
        fileInfo = Info(entry, entry.Size);
        return STATUS_SUCCESS;
    }

    /// <summary>
    /// One open directory's entries in a fixed order as they arrive, so WinFsp's marker continues where the last answer ended.
    /// A cached folder is one sorted batch; a folder still loading answers each Graph page as soon as it is in.
    /// </summary>
    internal sealed class DirectoryReader : IDisposable
    {
        readonly IAsyncEnumerator<IReadOnlyList<FsEntry>> batches;
        readonly List<(string Name, FsEntry? Entry)> entries = [];
        readonly Dictionary<string, int> positions = new(StringComparer.OrdinalIgnoreCase);
        readonly Lock gate = new();
        bool done;

        /// <summary>The entries are in <see cref="NameOrder"/>: false once a batch starts before the end of the one before it.</summary>
        bool sorted = true;

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

        /// <summary>
        /// Fills WinFsp's buffer with the entries after <paramref name="marker"/> (from the start when null) that are here, and the end
        /// marker when the listing is complete. False when none is here yet: wait for <see cref="MoreAsync"/> and try again. A marker
        /// this reader never listed waits for the whole listing, then continues after its sorted place (<see cref="After"/>).
        /// </summary>
        public bool TryAnswer(string? marker, IntPtr buffer, uint length, out uint transferred)
        {
            transferred = 0;
            lock (gate)
            {
                Arrived();
                var start = marker is null ? 0 : positions.TryGetValue(marker, out var position) ? position + 1 : done ? After(marker) : -1;
                if (start < 0 || start >= entries.Count && !done)
                {
                    return false;
                }
                for (var index = start; index < entries.Count; index++)
                {
                    var (name, entry) = entries[index];
                    var info = entry is null ? new FileInfo { FileAttributes = AttributeDirectory } : Info(entry, entry.Size);
                    if (!WinFspNative.AddEntry(name, info, buffer, length, ref transferred))
                    {
                        return true;
                    }
                }
                if (done)
                {
                    WinFspNative.AddEnd(buffer, length, ref transferred);
                }
                return true;
            }
        }

        /// <summary>Completes when the next batch is in (or the listing ended); throws when the listing failed.</summary>
        public Task MoreAsync()
        {
            lock (gate)
            {
                return done ? Task.CompletedTask : moving ??= batches.MoveNextAsync().AsTask();
            }
        }

        /// <summary>
        /// Takes in every batch that is here and starts fetching the next (one MoveNextAsync at a time), so a cached folder is one
        /// answer. A failed listing throws here, for every answer on this handle. Under gate.
        /// </summary>
        void Arrived()
        {
            while (!done && (moving ??= batches.MoveNextAsync().AsTask()) is { IsCompleted: true } arrived)
            {
                var more = arrived.GetAwaiter().GetResult();
                moving = null;
                if (!more)
                {
                    done = true;
                    return;
                }
                var batch = batches.Current;
                // Usually sorted already (cached listings are); sorted here only when not
                for (var i = 1; i < batch.Count; i++)
                {
                    if (NameOrder.Compare(batch[i - 1].Name, batch[i].Name) > 0)
                    {
                        batch = [.. batch.OrderBy(e => e.Name, NameOrder)];
                        break;
                    }
                }
                foreach (var entry in batch)
                {
                    Add(entry.Name, entry);
                }
            }
        }

        void Add(string name, FsEntry? entry)
        {
            if (positions.TryAdd(name, entries.Count))
            {
                sorted = sorted && (entries.Count == 0 || NameOrder.Compare(entries[^1].Name, name) < 0);
                entries.Add((name, entry));
            }
        }

        /// <summary>
        /// Where a scan continues after a name this reader never listed (deleted before this listing, or from another handle's
        /// answer): after the names that sort before it, as NTFS does, instead of ending the scan. Batches that aren't in one sorted
        /// order are sorted into one first; the scan continues in that order. Under gate, with the listing complete.
        /// </summary>
        int After(string marker)
        {
            if (!sorted)
            {
                entries.Sort((a, b) => NameOrder.Compare(a.Name, b.Name));
                for (var i = 0; i < entries.Count; i++)
                {
                    positions[entries[i].Name] = i;
                }
                sorted = true;
            }
            var (low, high) = (0, entries.Count);
            while (low < high)
            {
                var middle = (low + high) / 2;
                (low, high) = NameOrder.Compare(entries[middle].Name, marker) < 0 ? (middle + 1, high) : (low, middle);
            }
            return low;
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
    /// The handle's current content, counted as in use until <see cref="DoneReading"/>: the staging file while the file is being
    /// changed, else the cached or streaming download of the version it follows; null for an empty file, which needs none. A file
    /// deleted while open keeps serving what the handle already had.
    /// </summary>
    async ValueTask<IContentSource?> ReadableAsync(Handle handle)
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
                    handle.Reading++;
                    return kept;
                }
            }
            throw;
        }
        if (entry.StagingPath is null && entry.Size == 0)
        {
            return null;
        }
        var version = VersionOf(entry);
        lock (handle.Gate)
        {
            if (handle.Source is not null && handle.SourceVersion == version)
            {
                handle.Reading++;
                return handle.Source;
            }
        }
        var source = await engine.OpenContentAsync(entry, default);
        lock (handle.Gate)
        {
            handle.Reading++;
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

    /// <summary>Ends a read's use of the handle's content; the last one running disposes the versions replaced meanwhile.</summary>
    static void DoneReading(Handle handle)
    {
        IContentSource[] idle = [];
        lock (handle.Gate)
        {
            if (--handle.Reading == 0 && handle.Retired.Count > 0)
            {
                idle = [.. handle.Retired];
                handle.Retired.Clear();
            }
        }
        foreach (var source in idle)
        {
            source.Dispose();
        }
    }

    static string VersionOf(FsEntry entry) => entry.StagingPath ?? $"{entry.ItemId}|{entry.ContentTag}";

    /// <summary>
    /// The staging file a write goes to, with the engine's write lease taken: dispose it after the write and MarkDirty. Null when the
    /// handle has to stage first: a handle whose content was uploaded stages again from the item it went into (a conflict copy
    /// included), never from a version it didn't write; once its content was deleted or replaced, Follow throws (STATUS_FILE_INVALID).
    /// </summary>
    (SafeFileHandle Content, FsEntry Entry, IDisposable Lease)? Writable(Handle handle)
    {
        var entry = handle.Entry = engine.Follow(handle.Entry);
        lock (handle.Gate)
        {
            return handle.Wrote && handle.Content is { } content && handle.ContentPath == entry.StagingPath && engine.BeginWrite(entry) is { } lease
                ? (content, entry, lease)
                : null;
        }
    }

    /// <summary><see cref="Writable"/>, staging as often as needed, on this thread.</summary>
    (SafeFileHandle Content, FsEntry Entry, IDisposable Lease) BeginWrite(Handle handle)
    {
        while (true)
        {
            if (Writable(handle) is { } writable)
            {
                return writable;
            }
            Stage(handle, truncate: false);
        }
    }

    void Stage(Handle handle, bool truncate) => Run(StageAsync(handle, truncate));

    /// <summary>
    /// Stages the handle's file and opens the staging file. Staged content deleted, replaced or uploaded through another handle before
    /// it is opened (no namespace lock orders them) is followed: staged again where it went, else Follow throws (STATUS_FILE_INVALID).
    /// </summary>
    async Task StageAsync(Handle handle, bool truncate)
    {
        while (true)
        {
            var staged = await engine.StageAsync(handle.Entry, truncate, default);
            lock (handle.Gate)
            {
                handle.Entry = staged;
                if (handle.ContentPath != staged.StagingPath || handle.Content is null)
                {
                    handle.Content?.Dispose();
                    handle.Content = null;
                    handle.ContentPath = null;
                    try
                    {
                        handle.Content = OpenStaging(staged.StagingPath!);
                    }
                    catch (FileNotFoundException)
                    {
                        handle.Entry = engine.Follow(staged);
                        continue;
                    }
                    handle.ContentPath = staged.StagingPath;
                }
                handle.Wrote = true;
                return;
            }
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
        const int KindOffset = 4, AccessTokenOffset = 40, ShareAccessOffset = 56, TokenUser = 1;
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

        /// <summary>The FILE_SHARE_* flags of the create request being served (Req.Create.ShareAccess); null outside one.</summary>
        public static uint? ShareAccess()
        {
            var context = (byte**)OperationContext();
            if (context is null || *context is null || *(uint*)(*context + KindOffset) != CreateKind)
            {
                return null;
            }
            return *(uint*)(*context + ShareAccessOffset);
        }

        /// <summary>The requesting process of the create request being served (high half of Req.Create.AccessToken); 0 outside one.</summary>
        public static uint ProcessId()
        {
            var context = (byte**)OperationContext();
            if (context is null || *context is null || *(uint*)(*context + KindOffset) != CreateKind)
            {
                return 0;
            }
            return (uint)(*(ulong*)(*context + AccessTokenOffset) >> 32);
        }

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
