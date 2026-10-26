using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

/// <summary>A place Explorer shows the volume: Prefix ("M:\", "\\VFS365\ann\", "Z:\") stands for the volume path Target ("\", "\Sites\Finance\Documents\").</summary>
public readonly record struct ShellRoot(string Prefix, string Target);

/// <summary>
/// The shell section's layout (vfs365shell.dll reads it): a 64-byte header, the roots, the menu's strings and a hash table of file states
/// by volume path. All little-endian. Built in a private buffer, then copied into the section in one short seqlock window.
/// </summary>
public static class ShellImage
{
    public const uint Magic = 0x35363356;
    public const uint Version = 1;
    public const int SectionSize = 2 * 1024 * 1024;
    public const int HeaderSize = 64;
    public const int SlotCount = 65536;
    public const int SlotSize = 16;

    /// <summary>Entries at most: 70% of the slots.</summary>
    public const int MaxEntries = SlotCount * 7 / 10;

    public const uint MenuFlag = 1, OverlaysFlag = 2;

    /// <summary>Header field offsets.</summary>
    public const int SequenceAt = 8, ProcessAt = 12, FlagsAt = 16, RootsOffsetAt = 20, RootsCountAt = 24, StringsOffsetAt = 28, StringsCountAt = 32,
        SlotsOffsetAt = 36, SlotCountAt = 40, EntryCountAt = 44;

    /// <summary>
    /// Writes the image into <paramref name="image"/> (at least <see cref="SectionSize"/> bytes) and returns its length. Sequence stays 0:
    /// the section keeps its own. Entries beyond <see cref="MaxEntries"/> and repeated hashes are left out (callers rank them first).
    /// </summary>
    public static int Build(Span<byte> image, uint processId, uint flags, IReadOnlyList<ShellRoot> roots, IReadOnlyList<(string Key, string Value)> strings,
        IEnumerable<(ulong Hash, FileState State)> entries)
    {
        if (image.Length < SectionSize)
        {
            throw new ArgumentException("The image needs the section's size", nameof(image));
        }
        image[..HeaderSize].Clear();
        const int slotsBytes = SlotCount * SlotSize;
        var limit = SectionSize - slotsBytes - 15; // roots and strings end here at most, so the slots still fit
        var at = HeaderSize;
        var rootCount = 0;
        foreach (var root in roots)
        {
            if (!TryPair(image, ref at, limit, root.Prefix, root.Target))
            {
                break;
            }
            rootCount++;
        }
        var stringsAt = at;
        var stringCount = 0;
        foreach (var (key, value) in strings)
        {
            if (!TryPair(image, ref at, limit, key, value))
            {
                break;
            }
            stringCount++;
        }
        var slotsAt = (at + 15) & ~15;
        image[at..slotsAt].Clear();
        var slots = image.Slice(slotsAt, slotsBytes);
        slots.Clear();
        var count = 0;
        foreach (var (hash, state) in entries)
        {
            if (count == MaxEntries)
            {
                break;
            }
            if (hash == 0 || state == FileState.OnlineOnly)
            {
                continue;
            }
            for (var index = (int)(hash & (SlotCount - 1)); ; index = (index + 1) & (SlotCount - 1))
            {
                var slot = slots.Slice(index * SlotSize, SlotSize);
                var held = BinaryPrimitives.ReadUInt64LittleEndian(slot);
                if (held == hash)
                {
                    break; // the same key: the first (higher ranked) stays
                }
                if (held == 0)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(slot, hash);
                    BinaryPrimitives.WriteUInt32LittleEndian(slot[8..], (uint)state);
                    count++;
                    break;
                }
            }
        }
        Write(image, 0, Magic);
        Write(image, 4, Version);
        Write(image, ProcessAt, processId);
        Write(image, FlagsAt, flags);
        Write(image, RootsOffsetAt, HeaderSize);
        Write(image, RootsCountAt, (uint)rootCount);
        Write(image, StringsOffsetAt, (uint)stringsAt);
        Write(image, StringsCountAt, (uint)stringCount);
        Write(image, SlotsOffsetAt, (uint)slotsAt);
        Write(image, SlotCountAt, SlotCount);
        Write(image, EntryCountAt, (uint)count);
        return slotsAt + slotsBytes;
    }

    static void Write(Span<byte> image, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image[at..], value);

    /// <summary>u16 length of each in chars, then both in UTF-16, padded to 4 bytes. False when it doesn't fit before <paramref name="limit"/>.</summary>
    static bool TryPair(Span<byte> image, ref int at, int limit, string first, string second)
    {
        if (first.Length > ushort.MaxValue || second.Length > ushort.MaxValue)
        {
            return true; // never one this long; left out
        }
        var length = (4 + (first.Length + second.Length) * 2 + 3) & ~3;
        if (at + length > limit)
        {
            return false;
        }
        var record = image.Slice(at, length);
        record.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(record, (ushort)first.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(record[2..], (ushort)second.Length);
        Encoding.Unicode.GetBytes(first, record[4..]);
        Encoding.Unicode.GetBytes(second, record[(4 + first.Length * 2)..]);
        at += length;
        return true;
    }

    /// <summary>The table's key for a volume path: uppercased as Windows does it invariantly (the reader uppercases the same way), hashed.</summary>
    public static ulong KeyHash(string volumePath) => Hash(Upper(volumePath));

    /// <summary>FNV-1a 64 over the UTF-16 code units, low byte first; 0 (the empty slot) becomes 1.</summary>
    public static ulong Hash(string key)
    {
        const ulong prime = 0x100000001b3;
        var hash = 0xcbf29ce484222325UL;
        foreach (var c in key)
        {
            hash = (hash ^ (byte)c) * prime;
            hash = (hash ^ (byte)(c >> 8)) * prime;
        }
        return hash == 0 ? 1 : hash;
    }

    /// <summary>LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE): what the reader uses, so both sides agree on every character.</summary>
    public static unsafe string Upper(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }
        Span<char> buffer = text.Length <= 512 ? stackalloc char[text.Length] : new char[text.Length];
        fixed (char* source = text)
        fixed (char* target = buffer)
        {
            if (LCMapStringEx("", 0x200, source, text.Length, target, buffer.Length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != text.Length)
            {
                throw new Win32Exception();
            }
        }
        return new string(buffer);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern unsafe int LCMapStringEx(string localeName, uint flags, char* source, int sourceLength, char* target, int targetLength,
        IntPtr versionInfo, IntPtr reserved, IntPtr sortHandle);
}

/// <summary>
/// The pagefile-backed section Local\VFS365.Shell.&lt;SID&gt; that vfs365shell.dll in Explorer reads: only this user and SYSTEM may open it,
/// and it is owned by this user. One left by an earlier agent (Explorer keeps it open) is used again. Writes go through a seqlock:
/// Sequence is odd while the image is copied in.
/// </summary>
public sealed unsafe class ShellSection : IDisposable
{
    readonly Lock gate = new();
    IntPtr mapping, view;

    ShellSection(IntPtr mapping, IntPtr view) => (this.mapping, this.view) = (mapping, view);

    public static string NameFor(SecurityIdentifier user) => $@"Local\VFS365.Shell.{user.Value}";

    /// <summary>Creates or opens the section. Throws when Windows refuses, or when one of that name belongs to someone else.</summary>
    public static ShellSection Open(string name, SecurityIdentifier user)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:{user.Value}D:P(A;;GA;;;{user.Value})(A;;GA;;;SY)", 1, out var descriptor, IntPtr.Zero))
        {
            throw new Win32Exception();
        }
        IntPtr mapping;
        bool existed;
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            mapping = CreateFileMappingW(new IntPtr(-1), ref attributes, 0x04, 0, ShellImage.SectionSize, name); // PAGE_READWRITE
            existed = Marshal.GetLastWin32Error() == 183; // ERROR_ALREADY_EXISTS
            if (mapping == IntPtr.Zero)
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
        try
        {
            if (existed && OwnerOf(mapping) is { } owner && owner != user)
            {
                throw new UnauthorizedAccessException($"{name} belongs to {owner.Value}");
            }
            var view = MapViewOfFile(mapping, 0x02 | 0x04, 0, 0, ShellImage.SectionSize); // FILE_MAP_WRITE | FILE_MAP_READ
            if (view == IntPtr.Zero)
            {
                throw new Win32Exception();
            }
            return new ShellSection(mapping, view);
        }
        catch
        {
            CloseHandle(mapping);
            throw;
        }
    }

    static SecurityIdentifier? OwnerOf(IntPtr handle)
    {
        if (GetSecurityInfo(handle, 6, 1, out var owner, out _, out _, out _, out var descriptor) != 0) // SE_KERNEL_OBJECT, OWNER_SECURITY_INFORMATION
        {
            return null;
        }
        try
        {
            return owner == IntPtr.Zero ? null : new SecurityIdentifier(owner);
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>Copies an image in (<see cref="ShellImage.Build"/>); its Sequence bytes are skipped.</summary>
    public void Write(ReadOnlySpan<byte> image)
    {
        if (image.Length < ShellImage.HeaderSize || image.Length > ShellImage.SectionSize)
        {
            throw new ArgumentException("Not a section image", nameof(image));
        }
        lock (gate)
        {
            var bytes = Begin(out var even);
            image[..ShellImage.SequenceAt].CopyTo(new Span<byte>(bytes, ShellImage.SequenceAt));
            image[(ShellImage.SequenceAt + 4)..].CopyTo(new Span<byte>(bytes + ShellImage.SequenceAt + 4, image.Length - ShellImage.SequenceAt - 4));
            End(even);
        }
    }

    /// <summary>A clean stop: no menu, no overlays, no roots.</summary>
    public void Clear()
    {
        lock (gate)
        {
            var bytes = Begin(out var even);
            *(uint*)(bytes + ShellImage.FlagsAt) = 0;
            *(uint*)(bytes + ShellImage.RootsCountAt) = 0;
            *(uint*)(bytes + ShellImage.EntryCountAt) = 0;
            End(even);
        }
    }

    ref int Sequence => ref *(int*)((byte*)view + ShellImage.SequenceAt);

    /// <summary>Sequence to odd, so readers retry. Called under the gate.</summary>
    byte* Begin(out int even)
    {
        ObjectDisposedException.ThrowIf(view == IntPtr.Zero, this);
        even = Volatile.Read(ref Sequence) & ~1; // odd: an earlier writer stopped halfway
        Volatile.Write(ref Sequence, unchecked(even + 1));
        Interlocked.MemoryBarrier();
        return (byte*)view;
    }

    /// <summary>Sequence to the next even value: the image is whole.</summary>
    void End(int even)
    {
        Interlocked.MemoryBarrier();
        Volatile.Write(ref Sequence, unchecked(even + 2));
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (view != IntPtr.Zero)
            {
                UnmapViewOfFile(view);
                view = IntPtr.Zero;
            }
            if (mapping != IntPtr.Zero)
            {
                CloseHandle(mapping);
                mapping = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("advapi32.dll")]
    static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl,
        out IntPtr sacl, out IntPtr descriptor);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileMappingW(IntPtr file, ref SecurityAttributes attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll")]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}
