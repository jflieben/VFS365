using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading.Channels;
using Fsp;
using Fsp.Interop;
using Vfs365.Core.Drive;

namespace Vfs365.Drive.WinFsp;

/// <summary>A mounted VFS365 volume. Dispose to unmount.</summary>
public sealed class DriveHost : IDisposable
{
    /// <summary>ERROR_FILE_EXISTS as WinFsp reports it when another volume holds the same UNC prefix.</summary>
    const int FileExists = unchecked((int)0x80070050);

    /// <summary>
    /// How long a request may wait for a free dispatcher thread before WinFsp fails it: WinFsp's maximum, up from its default 5 minutes.
    /// Once a thread took a request WinFsp neither cancels nor times it out (only the unmount does), so it doesn't bound an upload on
    /// close or rename, or any other callback: the engine's own timeouts do (no answer from Microsoft 365, a transfer without progress).
    /// </summary>
    static readonly TimeSpan IrpTimeout = TimeSpan.FromMinutes(10);

    /// <summary>
    /// WinFsp dispatcher threads. Reads, writes and listings that wait answer later, but opens, closes (uploads), renames and lookups
    /// still hold a thread while they wait for Microsoft 365.
    /// </summary>
    const uint ThreadCount = 64;

    readonly FileSystemHost host;
    readonly Channel<EngineChange> changes = Channel.CreateUnbounded<EngineChange>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task notifier;

    readonly Action<string>? log;

    DriveHost(FileSystemHost host, string? driveLetter, bool namespaceLocked, Action<string>? log)
    {
        (this.host, DriveLetter, NamespaceLocked, this.log) = (host, driveLetter, namespaceLocked, log);
        notifier = Task.Run(NotifyLoopAsync);
    }

    /// <summary>
    /// True when WinFsp's own operation guard serializes the namespace (asked for, or kept because the native layout didn't match);
    /// false when only the engine's per-key locking does.
    /// </summary>
    public bool NamespaceLocked { get; }

    /// <summary>Tells Windows (Explorer, apps watching folders) about a change that didn't come through this volume. Queued; never blocks.</summary>
    public void Notify(EngineChange change) => changes.Writer.TryWrite(change);

    /// <summary>NotifyBegin attempts per batch: it gives up after a second while renames are in progress.</summary>
    const int NotifyAttempts = 5;

    /// <summary>
    /// Sends queued changes in batches, off WinFsp's dispatcher threads (NotifyBegin waits for renames in progress). A batch NotifyBegin
    /// refuses is tried again a few times before it is dropped.
    /// </summary>
    async Task NotifyLoopAsync()
    {
        var batch = new List<NotifyInfo>();
        while (await changes.Reader.WaitToReadAsync())
        {
            await Task.Delay(50);
            while (batch.Count < 500 && changes.Reader.TryRead(out var change))
            {
                batch.Add(new NotifyInfo
                {
                    // WinFsp keeps upper-cased names on case-insensitive volumes when no normalized name is returned; watchers match only those
                    FileName = change.Path.ToUpperInvariant(),
                    Action = change.Kind switch { ChangeKind.Added => NotifyAction.Added, ChangeKind.Removed => NotifyAction.Removed, _ => NotifyAction.Modified },
                    Filter = change.Kind == ChangeKind.Modified
                        ? NotifyFilter.ChangeLastWrite | NotifyFilter.ChangeSize
                        : change.IsDirectory ? NotifyFilter.ChangeDirName : NotifyFilter.ChangeFileName,
                });
            }
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var begin = host.NotifyBegin(1000);
                    if (begin >= 0)
                    {
                        try
                        {
                            var status = host.Notify([.. batch]);
                            log?.Invoke($"notified {batch.Count} change(s), first {batch[0].FileName}: 0x{status:X8}");
                        }
                        finally
                        {
                            host.NotifyEnd();
                        }
                        break;
                    }
                    if (attempt == NotifyAttempts)
                    {
                        log?.Invoke($"notify of {batch.Count} change(s) skipped, NotifyBegin 0x{begin:X8} {NotifyAttempts} times");
                        break;
                    }
                }
                catch (Exception e)
                {
                    log?.Invoke($"notify failed: {e.GetType().Name}: {e.Message}");
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt));
            }
            batch.Clear();
        }
    }

    /// <summary>Such as "M:"; null when mounted without one (UNC path only).</summary>
    public string? DriveLetter { get; }

    /// <param name="mountPoint">Drive letter such as "M:", or null for the first free one counting down from Z.</param>
    /// <param name="keepDriveLetter">False removes the drive letter after mounting; the volume stays reachable through its UNC path.</param>
    /// <param name="uncPrefix">Network prefix with single backslashes, for example \VFS365\contoso.</param>
    /// <param name="winFspGuard">
    /// True keeps WinFsp's FINE operation guard, which holds a volume-wide lock exclusively around creates, overwrites, deletes and
    /// renames (so one slow lookup, folder delete or upload in them stops every open and listing). False, the default, replaces it
    /// with none: the engine orders changes per key. WinFsp's driver still holds every open while a rename runs, whatever the guard.
    /// </param>
    public static DriveHost Mount(DriveEngine engine, string? mountPoint, bool keepDriveLetter, string uncPrefix, string label,
        Action<string>? log, Action<string>? trace = null, bool winFspGuard = false)
    {
        var fileSystem = new Vfs365FileSystem(engine, label, log, trace, winFspGuard);
        var host = new FileSystemHost(fileSystem) { Prefix = uncPrefix };
        if (!WinFspNative.SetIrpTimeout(host, IrpTimeout))
        {
            log?.Invoke($"WinFsp's request timeout could not be set to {IrpTimeout.TotalMinutes:0} min; it stays at WinFsp's default");
        }
        var status = host.MountEx(mountPoint, ThreadCount, null, false, 0);
        if (status < 0)
        {
            host.Dispose();
            if (status == FileExists)
            {
                throw MountConflictException.For(uncPrefix, VolumeOwner($@"\{uncPrefix}\"), WindowsIdentity.GetCurrent().User!, status);
            }
            throw new IOException($"WinFsp could not mount {mountPoint ?? "a free drive letter"} (NTSTATUS 0x{status:X8}). Is WinFsp installed and the drive letter free?");
        }
        if (keepDriveLetter)
        {
            return new DriveHost(host, host.MountPoint(), !fileSystem.GuardReplaced, log);
        }
        RemoveMountPoint(host);
        return new DriveHost(host, null, !fileSystem.GuardReplaced, log);
    }

    public void Dispose()
    {
        changes.Writer.TryComplete();
        notifier.Wait(TimeSpan.FromSeconds(2));
        host.Unmount();
    }

    /// <summary>
    /// WinFsp's FspFileSystemRemoveMountPoint, which its .NET layer doesn't expose: drops the drive letter (WinFsp holds the only handle
    /// to it) while the volume and its UNC path stay. Network volumes can't be mounted on a folder instead (STATUS_NETWORK_ACCESS_DENIED).
    /// </summary>
    static unsafe void RemoveMountPoint(FileSystemHost host) => WinFspNative.RemoveMountPoint(WinFspNative.FileSystemOf(host));

    /// <summary>
    /// Owner of the root of the volume that holds a UNC path, or null when it can't be read (no access, or no answer within 5 s: the
    /// volume's file system may be hostile).
    /// </summary>
    static SecurityIdentifier? VolumeOwner(string root)
    {
        var read = Task.Run(() => new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier);
        try
        {
            return read.Wait(TimeSpan.FromSeconds(5)) ? read.Result : null;
        }
        catch (AggregateException)
        {
            return null;
        }
    }
}

/// <summary>
/// The UNC prefix is held by another volume. <see cref="OwnedByAnotherUser"/>: its root isn't owned by the current user (or can't
/// be read), so another user may have mounted it first to receive this user's files: raise a security alert. Otherwise the user's
/// own volume holds it, for example from another session. A file system written to deceive can report any owner, so false is not proof.
/// </summary>
public sealed class MountConflictException(string message, bool ownedByAnotherUser, string? owner) : IOException(message)
{
    public bool OwnedByAnotherUser { get; } = ownedByAnotherUser;

    /// <summary>The root's owner SID, or null when it couldn't be read.</summary>
    public string? Owner { get; } = owner;

    public static MountConflictException For(string uncPrefix, SecurityIdentifier? owner, SecurityIdentifier currentUser, int status)
    {
        if (owner == currentUser)
        {
            return new($@"\{uncPrefix} is already mounted by this user, in another session or program (NTSTATUS 0x{status:X8}).", false, owner.Value);
        }
        var who = owner is null ? "a volume whose owner can't be read" : $"a volume owned by {Account(owner)}";
        return new($@"\{uncPrefix} is held by {who}, not by this user. Files saved to that path would go to it, so VFS365 doesn't " +
            $"mount. Report this to your IT department (NTSTATUS 0x{status:X8}).", true, owner?.Value);
    }

    static string Account(SecurityIdentifier sid)
    {
        try
        {
            return $"{sid.Translate(typeof(NTAccount))} ({sid})";
        }
        catch (SystemException)
        {
            return sid.Value;
        }
    }
}
