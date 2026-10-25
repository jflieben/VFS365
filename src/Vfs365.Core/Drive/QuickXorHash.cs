using System.Buffers;
using System.Numerics;

namespace Vfs365.Core.Drive;

/// <summary>
/// OneDrive's quickXorHash (file.hashes.quickXorHash, learn.microsoft.com/onedrive/developer/code-snippets/quickxorhash): byte k is
/// XORed into a 160-bit block at bit 11k (mod 160), the length into the last 64 bits, Base64. Bytes 160 apart land on the same bits, so
/// they are XORed into one of 160 bytes first and placed once at the end. Tells whether an upload whose answer got lost went through.
/// </summary>
public sealed class QuickXorHash
{
    const int Width = 160, Shift = 11;
    readonly byte[] columns = new byte[Width];
    int at;
    long length;

    public void Append(ReadOnlySpan<byte> data)
    {
        length += data.Length;
        while (data.Length > 0)
        {
            var n = Math.Min(Width - at, data.Length);
            Xor(columns.AsSpan(at, n), data[..n]);
            at = (at + n) % Width;
            data = data[n..];
        }
    }

    static void Xor(Span<byte> into, ReadOnlySpan<byte> from)
    {
        var i = 0;
        for (; i + Vector<byte>.Count <= from.Length; i += Vector<byte>.Count)
        {
            (new Vector<byte>(into[i..]) ^ new Vector<byte>(from[i..])).CopyTo(into[i..]);
        }
        for (; i < from.Length; i++)
        {
            into[i] ^= from[i];
        }
    }

    public string Finish()
    {
        Span<byte> block = stackalloc byte[Width / 8];
        block.Clear();
        for (var column = 0; column < Width; column++)
        {
            var bit = column * Shift % Width;
            var value = (uint)columns[column] << (bit % 8);
            block[bit / 8] ^= (byte)value;
            block[(bit / 8 + 1) % block.Length] ^= (byte)(value >> 8);
        }
        for (var i = 0; i < 8; i++)
        {
            block[block.Length - 8 + i] ^= (byte)(length >> (8 * i));
        }
        return Convert.ToBase64String(block);
    }

    public static string Of(ReadOnlySpan<byte> data)
    {
        var hash = new QuickXorHash();
        hash.Append(data);
        return hash.Finish();
    }

    /// <summary>The hash of a file's content as it is now.</summary>
    public static string OfFile(string path)
    {
        var hash = new QuickXorHash();
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            for (long offset = 0; RandomAccess.Read(file, buffer, offset) is var read and > 0; offset += read)
            {
                hash.Append(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        return hash.Finish();
    }
}
