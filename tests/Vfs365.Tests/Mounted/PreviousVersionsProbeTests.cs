using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Fsp;
using Microsoft.Win32.SafeHandles;
using Xunit.Abstractions;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace Vfs365.Tests.Mounted;

/// <summary>
/// Feasibility of Explorer's Previous Versions on the drive (feature idea A2), measured on a probe volume: a network file system
/// shows versions when FSCTL_SRV_ENUMERATE_SNAPSHOTS on a handle answers with @GMT tokens and paths with an @GMT segment open the
/// version. WinFsp 2.1 forwards only device controls of custom device types (bit 0x8000) with METHOD_BUFFERED, and no unknown
/// file system controls, so that control never reaches the file system; @GMT paths arrive as plain names.
/// </summary>
[Collection(MountedCollection.Name)]
public sealed class PreviousVersionsProbeTests(ITestOutputHelper output)
{
    const uint EnumerateSnapshots = 0x00144064; // FSCTL_SRV_ENUMERATE_SNAPSHOTS: FILE_DEVICE_NETWORK_FILE_SYSTEM, 25, buffered, read
    const uint CustomControl = 0x80002004; // a custom device type, METHOD_BUFFERED, any access: what WinFsp does forward

    /// <summary>One folder and one file; records the names and control codes that reach it.</summary>
    sealed class ProbeFileSystem : FileSystemBase
    {
        public ConcurrentQueue<string> Names { get; } = new();
        public ConcurrentQueue<uint> Controls { get; } = new();

        public override int Init(object host0)
        {
            var host = (FileSystemHost)host0;
            host.SectorSize = 4096;
            host.SectorsPerAllocationUnit = 1;
            host.CaseSensitiveSearch = false;
            host.CasePreservedNames = true;
            host.UnicodeOnDisk = true;
            host.FileSystemName = "VFS365";
            host.DeviceControl = true;
            return STATUS_SUCCESS;
        }

        public override int GetVolumeInfo(out VolumeInfo volumeInfo)
        {
            volumeInfo = default;
            volumeInfo.TotalSize = volumeInfo.FreeSize = 1UL << 30;
            return STATUS_SUCCESS;
        }

        static bool Known(string name) => name is "\\" or "\\file.txt";

        public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
        {
            Names.Enqueue(fileName);
            fileAttributes = fileName == "\\" ? 0x10u : 0x80u;
            return Known(fileName) ? STATUS_SUCCESS : STATUS_OBJECT_NAME_NOT_FOUND;
        }

        public override int Open(string fileName, uint createOptions, uint grantedAccess, out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
        {
            Names.Enqueue(fileName);
            (fileNode, fileDesc, normalizedName) = (null, fileName, null);
            fileInfo = new FileInfo { FileAttributes = fileName == "\\" ? 0x10u : 0x80u, FileSize = fileName == "\\" ? 0UL : 5UL };
            return Known(fileName) ? STATUS_SUCCESS : STATUS_OBJECT_NAME_NOT_FOUND;
        }

        public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
        {
            fileInfo = new FileInfo { FileAttributes = (string)fileDesc == "\\" ? 0x10u : 0x80u, FileSize = (string)fileDesc == "\\" ? 0UL : 5UL };
            return STATUS_SUCCESS;
        }

        public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker, IntPtr buffer, uint length, out uint bytesTransferred)
        {
            bytesTransferred = 0;
            return STATUS_SUCCESS;
        }

        public override int Control(object fileNode, object fileDesc, uint controlCode, IntPtr inputBuffer, uint inputBufferLength,
            IntPtr outputBuffer, uint outputBufferLength, out uint bytesTransferred)
        {
            Controls.Enqueue(controlCode);
            bytesTransferred = 0;
            return STATUS_SUCCESS;
        }
    }

    [MountedFact]
    public void Snapshot_enumeration_never_reaches_the_file_system_and_GMT_paths_arrive_as_plain_names()
    {
        var probe = new ProbeFileSystem();
        var share = $"p{Guid.NewGuid():N}"[..12];
        var host = new FileSystemHost(probe) { Prefix = $@"\VFS365T\{share}" };
        Assert.True(host.MountEx(null, 4, null, false, 0) >= 0);
        try
        {
            var root = $@"\\VFS365T\{share}";
            using var folder = Native.OpenFolder(root);
            var buffer = new byte[4096];

            var custom = DeviceIoControl(folder, CustomControl, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError();
            var asDevice = DeviceIoControl(folder, EnumerateSnapshots, IntPtr.Zero, 0, buffer, buffer.Length, out _, IntPtr.Zero) ? 0 : Marshal.GetLastPInvokeError();
            var asFileSystem = NtFsControlFile(folder, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out _, EnumerateSnapshots, IntPtr.Zero, 0, buffer, buffer.Length);
            output.WriteLine($"custom control: error {custom}; FSCTL_SRV_ENUMERATE_SNAPSHOTS as device control: error {asDevice}, " +
                $"as file system control: NTSTATUS 0x{asFileSystem:X8}; controls reaching the file system: [{string.Join(", ", probe.Controls.Select(c => $"0x{c:X8}"))}]");
            Assert.Equal(0, custom);
            Assert.Equal([CustomControl], probe.Controls);
            Assert.NotEqual(0, asDevice);
            Assert.NotEqual(0, asFileSystem);

            Assert.False(File.Exists($@"{root}\@GMT-2026.10.01-12.00.00\file.txt"));
            Assert.False(File.Exists($@"{root}\file.txt@GMT-2026.10.01-12.00.00"));
            output.WriteLine($"names reaching the file system: {string.Join(", ", probe.Names.Distinct())}");
            Assert.Contains(@"\@GMT-2026.10.01-12.00.00\file.txt", probe.Names);
        }
        finally
        {
            host.Unmount();
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("ntdll.dll")]
    static extern int NtFsControlFile(SafeFileHandle file, IntPtr evt, IntPtr apcRoutine, IntPtr apcContext, out IoStatus status, uint code,
        IntPtr input, int inputSize, byte[] output, int outputSize);

    [StructLayout(LayoutKind.Sequential)]
    struct IoStatus
    {
        public IntPtr Status;
        public IntPtr Information;
    }
}
