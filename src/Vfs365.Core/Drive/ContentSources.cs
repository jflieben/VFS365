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

    public long Length => RandomAccess.GetLength(file);

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) =>
        ValueTask.FromResult(offset >= Length ? 0 : RandomAccess.Read(file, buffer.Span, offset));

    public void Dispose() => file.Dispose();
}

/// <summary>No content: an empty file the cache couldn't keep.</summary>
internal sealed class EmptyContent : IContentSource
{
    public static readonly EmptyContent Instance = new();

    public long Length => 0;

    public ValueTask<int> ReadAsync(long offset, Memory<byte> buffer, CancellationToken ct) => ValueTask.FromResult(0);

    public void Dispose()
    {
    }
}

internal static class CacheFileHandle
{
    const uint GenericRead = 0x80000000, FileWriteAttributes = 0x100;

    /// <summary>
    /// Opens a complete cache file for reading. With <paramref name="touch"/> its last access time becomes that, on Windows through a
    /// handle that may also write attributes (no second open by path).
    /// </summary>
    public static SafeFileHandle OpenRead(string path, DateTime? touch)
    {
        if (touch is { } at && OperatingSystem.IsWindows())
        {
            var handle = CreateFileW(path, GenericRead | FileWriteAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                Touch(() => File.SetLastAccessTimeUtc(handle, at));
                return handle;
            }
            handle.Dispose(); // opened the usual way below, which throws what fits (gone, access denied)
        }
        var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (touch is { } then && !OperatingSystem.IsWindows())
        {
            Touch(() => File.SetLastAccessTimeUtc(path, then));
        }
        return file;
    }

    static void Touch(Action set)
    {
        try
        {
            set();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);
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
