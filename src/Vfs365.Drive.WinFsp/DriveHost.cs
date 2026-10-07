using System.Reflection;
using System.Runtime.InteropServices;
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

    readonly FileSystemHost host;
    readonly Channel<EngineChange> changes = Channel.CreateUnbounded<EngineChange>(new UnboundedChannelOptions { SingleReader = true });
    readonly Task notifier;

    readonly Action<string>? log;

    DriveHost(FileSystemHost host, string? driveLetter, Action<string>? log)
    {
        (this.host, DriveLetter, this.log) = (host, driveLetter, log);
        notifier = Task.Run(NotifyLoopAsync);
    }

    /// <summary>Tells Windows (Explorer, apps watching folders) about a change that didn't come through this volume. Queued; never blocks.</summary>
    public void Notify(EngineChange change) => changes.Writer.TryWrite(change);

    /// <summary>Sends queued changes in batches, off WinFsp's dispatcher threads (NotifyBegin waits for renames in progress).</summary>
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
                }
                else
                {
                    log?.Invoke($"notify skipped, NotifyBegin 0x{begin:X8}");
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"notify failed: {e.GetType().Name}: {e.Message}");
            }
            batch.Clear();
        }
    }

    /// <summary>Such as "M:"; null when mounted without one (UNC path only).</summary>
    public string? DriveLetter { get; }

    /// <param name="mountPoint">Drive letter such as "M:", or null for the first free one counting down from Z.</param>
    /// <param name="keepDriveLetter">False removes the drive letter after mounting; the volume stays reachable through its UNC path.</param>
    /// <param name="uncPrefix">Network prefix with single backslashes, for example \VFS365\contoso.</param>
    public static DriveHost Mount(DriveEngine engine, string? mountPoint, bool keepDriveLetter, string uncPrefix, string label,
        Action<string>? log, Action<string>? trace = null)
    {
        var host = new FileSystemHost(new Vfs365FileSystem(engine, label, log, trace)) { Prefix = uncPrefix };
        var status = host.MountEx(mountPoint, 16, null, false, 0);
        if (status < 0)
        {
            host.Dispose();
            throw new IOException(status == FileExists
                ? $@"\{uncPrefix} is already mounted on this machine, by another session or program (NTSTATUS 0x{status:X8})."
                : $"WinFsp could not mount {mountPoint ?? "a free drive letter"} (NTSTATUS 0x{status:X8}). Is WinFsp installed and the drive letter free?");
        }
        if (keepDriveLetter)
        {
            return new DriveHost(host, host.MountPoint(), log);
        }
        RemoveMountPoint(host);
        return new DriveHost(host, null, log);
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
}
