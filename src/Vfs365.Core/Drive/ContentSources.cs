using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Vfs365.Core.Drive;

/// <summary>Readable content of one file.</summary>
public interface IContentSource : IDisposable
{
    long Length { get; }

    ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct);
}

/// <summary>Content already on local disk: a complete cache file or a staging file.</summary>
internal sealed class FileContent(string path) : IContentSource
{
    readonly SafeFileHandle file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    public string Path { get; } = path;

    public long Length => RandomAccess.GetLength(file);

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) =>
        ValueTask.FromResult(offset >= Length ? 0 : RandomAccess.Read(file, buffer.Span, offset));

    public void Dispose() => file.Dispose();
}

internal static class SparseFile
{
    /// <summary>Unwritten ranges take no disk space and writing far into the file needs no zero-filling.</summary>
    public static void Mark(SafeFileHandle file)
    {
        if (OperatingSystem.IsWindows())
        {
            DeviceIoControl(file, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); // FSCTL_SET_SPARSE
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize,
        out uint returned, IntPtr overlapped);
}
