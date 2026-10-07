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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr GetModuleHandle(string moduleName);
}
