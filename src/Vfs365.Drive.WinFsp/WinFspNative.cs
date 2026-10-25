using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fsp;
using FileInfo = Fsp.Interop.FileInfo;

namespace Vfs365.Drive.WinFsp;

/// <summary>WinFsp DLL functions its .NET layer keeps internal, called through their exports.</summary>
static unsafe class WinFspNative
{
    static readonly IntPtr Module = GetModuleHandle(RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "winfsp-a64.dll",
        Architecture.X86 => "winfsp-x86.dll",
        _ => "winfsp-x64.dll",
    });

    static readonly delegate* unmanaged<byte*, IntPtr, uint, uint*, byte> AddDirInfo =
        (delegate* unmanaged<byte*, IntPtr, uint, uint*, byte>)NativeLibrary.GetExport(Module, "FspFileSystemAddDirInfo");

    public static readonly delegate* unmanaged<IntPtr, void> RemoveMountPoint =
        (delegate* unmanaged<IntPtr, void>)NativeLibrary.GetExport(Module, "FspFileSystemRemoveMountPoint");

    /// <summary>sizeof(FSP_FSCTL_DIR_INFO) without the name: Size (2, padded to 8), FSP_FSCTL_FILE_INFO (72), padding union (24).</summary>
    const int DirInfoHeader = 104;

    /// <summary>Adds one entry to a ReadDirectory buffer; false when it is full.</summary>
    public static bool AddEntry(string name, in FileInfo info, IntPtr buffer, uint length, ref uint transferred)
    {
        var size = DirInfoHeader + name.Length * 2;
        Span<byte> entry = size <= 1024 ? stackalloc byte[size] : new byte[size];
        entry.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)size);
        MemoryMarshal.Write(entry[8..], in info);
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(entry[DirInfoHeader..]);
        fixed (byte* pointer = entry)
        fixed (uint* bytes = &transferred)
        {
            return AddDirInfo(pointer, buffer, length, bytes) != 0;
        }
    }

    /// <summary>Adds the end-of-directory marker; without it WinFsp asks again, continuing after the last name.</summary>
    public static void AddEnd(IntPtr buffer, uint length, ref uint transferred)
    {
        fixed (uint* bytes = &transferred)
        {
            AddDirInfo(null, buffer, length, bytes);
        }
    }

    /// <summary>The native FSP_FILE_SYSTEM of a host, which the .NET layer keeps private.</summary>
    public static IntPtr FileSystemOf(FileSystemHost host) =>
        (IntPtr)typeof(FileSystemHost).GetField("_FileSystemPtr", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(host)!;

    /// <summary>
    /// FSP_FILE_SYSTEM (winfsp.h, 792 bytes on 64-bit): Version, UserContext, VolumeName (256 WCHARs), VolumeHandle, EnterOperation,
    /// LeaveOperation, Operations (22), Interface, DispatcherThread, DispatcherThreadCount, DispatcherResult, MountPoint, MountHandle,
    /// DebugLog, OpGuardStrategy.
    /// </summary>
    static readonly int EnterOffset = 3 * IntPtr.Size + 512, LeaveOffset = 4 * IntPtr.Size + 512,
        DispatcherOffset = 28 * IntPtr.Size + 512, StrategyOffset = 31 * IntPtr.Size + 524;

    /// <summary>
    /// Replaces WinFsp's FINE operation guard, which serializes the whole namespace (exclusive for Create other than plain opens,
    /// Overwrite, Cleanup with delete and Rename; shared for opens, listings and delete checks), with one that guards nothing: the engine
    /// locks per key. Called from Mounted, before the dispatcher starts. Checks the fields it relies on first and changes nothing when
    /// one doesn't match; returns why it kept WinFsp's guard, null when it replaced it. WinFsp's driver still serializes renames
    /// against every create (its FileRenameResource), whatever the guard.
    /// </summary>
    public static string? ReplaceOperationGuard(FileSystemHost host)
    {
        var fileSystem = (byte*)FileSystemOf(host);
        if (fileSystem is null)
        {
            return "no file system";
        }
        if (!NativeLibrary.TryGetExport(Module, "FspFileSystemOpEnter", out var fineEnter) || !NativeLibrary.TryGetExport(Module, "FspFileSystemOpLeave", out var fineLeave)
            || !NativeLibrary.TryGetExport(Module, "FspFileSystemSetOperationGuardF", out var setter))
        {
            return "this WinFsp lacks FspFileSystemOpEnter, FspFileSystemOpLeave or FspFileSystemSetOperationGuardF";
        }
        var setGuard = (delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)setter;
        var enter = *(IntPtr*)(fileSystem + EnterOffset);
        var leave = *(IntPtr*)(fileSystem + LeaveOffset);
        var strategy = *(int*)(fileSystem + StrategyOffset);
        var dispatcher = *(IntPtr*)(fileSystem + DispatcherOffset);
        if (enter != fineEnter || leave != fineLeave || strategy != 0 || dispatcher != IntPtr.Zero)
        {
            return $"unexpected file system fields (enter {enter == fineEnter}, leave {leave == fineLeave}, strategy {strategy}, dispatcher started {dispatcher != IntPtr.Zero})";
        }
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, int> none = &NoGuard;
        setGuard((IntPtr)fileSystem, (IntPtr)none, (IntPtr)none);
        if (*(IntPtr*)(fileSystem + EnterOffset) != (IntPtr)none || *(IntPtr*)(fileSystem + LeaveOffset) != (IntPtr)none)
        {
            setGuard((IntPtr)fileSystem, fineEnter, fineLeave);
            return "the new guard did not read back";
        }
        return null;
    }

    /// <summary>FSP_FILE_SYSTEM_OPERATION_GUARD that lets every operation through.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    static int NoGuard(IntPtr fileSystem, IntPtr request, IntPtr response) => 0;

    /// <summary>
    /// Sets VolumeParams.IrpTimeout (milliseconds; WinFsp accepts 1 to 10 minutes, otherwise uses its default), which the .NET layer
    /// doesn't expose. Before mounting; false when this winfsp.net build has no such field.
    /// </summary>
    public static bool SetIrpTimeout(FileSystemHost host, TimeSpan timeout)
    {
        const System.Reflection.BindingFlags Instance = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance;
        var paramsField = typeof(FileSystemHost).GetField("_VolumeParams", Instance);
        var timeoutField = paramsField?.FieldType.GetField("IrpTimeout", Instance);
        if (paramsField is null || timeoutField is null || timeoutField.FieldType != typeof(uint))
        {
            return false;
        }
        var volumeParams = paramsField.GetValue(host)!; // a boxed copy of the struct
        timeoutField.SetValue(volumeParams, (uint)timeout.TotalMilliseconds);
        paramsField.SetValue(host, volumeParams);
        return true;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string moduleName);
}
