using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Vfs365.Core.Drive;

/// <summary>
/// Encrypts cached file content with AES-256-GCM in 64 KiB chunks, so any range can be written (as the streaming download does) and
/// read on its own. Each cache file has a random nonce; each chunk is authenticated together with the file's cache name (drive, item
/// and version), its position and the content length, so a changed, moved or truncated cache file fails to read instead of serving
/// wrong bytes. Layout: content, one 16-byte tag per chunk, then nonce (12), content length (8) and a format marker (4).
/// </summary>
public sealed class CacheCipher
{
    public const int ChunkSize = 64 * 1024;
    const int TagSize = 16;
    const int NonceSize = 12;
    const int FooterSize = NonceSize + 8 + 4;
    static readonly uint Marker = BinaryPrimitives.ReadUInt32LittleEndian("VC01"u8);

    readonly ThreadLocal<AesGcm> aes, records;

    public CacheCipher(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The cache key is 32 bytes", nameof(key));
        }
        var copy = key.ToArray();
        aes = new ThreadLocal<AesGcm>(() => new AesGcm(copy, TagSize));
        // Records get a key of their own: their random nonces never share a key with the content's
        var recordKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, copy, 32, info: "vfs365 cache records"u8.ToArray());
        records = new ThreadLocal<AesGcm>(() => new AesGcm(recordKey, TagSize));
        KeyId = Convert.ToHexString(SHA256.HashData(copy))[..16];
    }

    /// <summary>Identifies the key without revealing it, so content written with another key is never read back as plaintext.</summary>
    public string KeyId { get; }

    /// <summary>Changes when the file layout does, so caches written by other versions are dropped at start.</summary>
    public const string Format = "gcm1";

    /// <summary>Bytes on disk for <paramref name="length"/> bytes of content.</summary>
    public static long StoredLength(long length) => length + Chunks(length) * TagSize + FooterSize;

    /// <summary>The content length a cache file of <paramref name="stored"/> bytes holds; -1 when no content length gives that size.</summary>
    public static long ContentLength(long stored)
    {
        var sealedBytes = stored - FooterSize;
        if (sealedBytes < 0)
        {
            return -1;
        }
        var (chunks, rest) = Math.DivRem(sealedBytes, ChunkSize + TagSize);
        var length = chunks * ChunkSize + (rest > TagSize ? rest - TagSize : 0);
        return StoredLength(length) == stored ? length : -1;
    }

    static long Chunks(long length) => (length + ChunkSize - 1) / ChunkSize;

    /// <summary>Starts a cache file: sets its size and writes the footer with a new nonce. Chunks are written with <see cref="SealedFile.Write"/>.</summary>
    internal SealedFile Create(SafeFileHandle file, long length, string name)
    {
        var footer = new byte[FooterSize];
        RandomNumberGenerator.Fill(footer.AsSpan(0, NonceSize));
        BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(NonceSize), length);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(NonceSize + 8), Marker);
        RandomAccess.SetLength(file, StoredLength(length));
        RandomAccess.Write(file, footer, StoredLength(length) - FooterSize);
        return new SealedFile(this, file, length, footer[..NonceSize], name);
    }

    /// <summary>Opens a complete cache file; throws <see cref="CacheDamagedException"/> when it isn't one of this format.</summary>
    internal SealedFile Open(SafeFileHandle file, string name)
    {
        var stored = RandomAccess.GetLength(file);
        var footer = new byte[FooterSize];
        if (stored < FooterSize || RandomAccess.Read(file, footer, stored - FooterSize) != FooterSize
            || BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(NonceSize + 8)) != Marker
            || BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(NonceSize)) is var length && (length < 0 || StoredLength(length) != stored))
        {
            throw new CacheDamagedException(name);
        }
        return new SealedFile(this, file, length, footer[..NonceSize], name);
    }

    /// <summary>Encrypts a short record (a line of the cache's name index) on its own, with a random nonce and a key derived for records.</summary>
    internal string SealRecord(string text)
    {
        var plain = System.Text.Encoding.UTF8.GetBytes(text);
        var result = new byte[NonceSize + plain.Length + TagSize];
        var nonce = result.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        records.Value!.Encrypt(nonce, plain, result.AsSpan(NonceSize, plain.Length), result.AsSpan(NonceSize + plain.Length), RecordPurpose);
        return Convert.ToBase64String(result);
    }

    /// <summary>The text of a record <see cref="SealRecord"/> made with this key; null when it is damaged or another key's.</summary>
    internal string? OpenRecord(string record)
    {
        try
        {
            var bytes = Convert.FromBase64String(record);
            if (bytes.Length < NonceSize + TagSize)
            {
                return null;
            }
            var plain = new byte[bytes.Length - NonceSize - TagSize];
            records.Value!.Decrypt(bytes.AsSpan(0, NonceSize), bytes.AsSpan(NonceSize, plain.Length), bytes.AsSpan(NonceSize + plain.Length), plain, RecordPurpose);
            return System.Text.Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is FormatException or AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    static readonly byte[] RecordPurpose = "vfs365 cache record"u8.ToArray();

    /// <summary>One cache file's content, encrypted chunk by chunk. Reads decrypt and verify whole chunks; the last one read is kept.</summary>
    internal sealed class SealedFile(CacheCipher cipher, SafeFileHandle file, long length, byte[] nonce, string name)
    {
        readonly Lock gate = new();
        // What each chunk's associated data starts with; the chunk number follows
        readonly byte[] associated = System.Text.Encoding.UTF8.GetBytes($"{name}|{length}|");
        byte[]? chunk, sealedChunk;
        long cached = -1;

        public long Length { get; } = length;

        long TagOffset(long index) => Length + index * TagSize;

        /// <summary>Encrypts and writes <paramref name="plain"/>, which starts at a chunk boundary and ends at one or at the end.</summary>
        public void Write(long offset, ReadOnlySpan<byte> plain)
        {
            var sealedBytes = ArrayPool<byte>.Shared.Rent(Math.Min(plain.Length, ChunkSize));
            try
            {
                Span<byte> tag = stackalloc byte[TagSize];
                Span<byte> chunkNonce = stackalloc byte[NonceSize];
                Span<byte> data = stackalloc byte[associated.Length + 20];
                for (var done = 0; done < plain.Length; done += ChunkSize)
                {
                    var part = plain.Slice(done, Math.Min(ChunkSize, plain.Length - done));
                    var index = (offset + done) / ChunkSize;
                    cipher.aes.Value!.Encrypt(Nonce(index, chunkNonce), part, sealedBytes.AsSpan(0, part.Length), tag, Associated(index, data));
                    RandomAccess.Write(file, sealedBytes.AsSpan(0, part.Length), offset + done);
                    RandomAccess.Write(file, tag, TagOffset(index));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(sealedBytes);
            }
        }

        /// <summary>Decrypts content at <paramref name="offset"/> into <paramref name="buffer"/>; the chunks must have been written.</summary>
        public int Read(long offset, Span<byte> buffer)
        {
            var total = (int)Math.Max(0, Math.Min(buffer.Length, Length - offset));
            lock (gate)
            {
                chunk ??= new byte[Math.Min(ChunkSize, Length)];
                for (var done = 0; done < total;)
                {
                    var index = (offset + done) / ChunkSize;
                    if (index != cached)
                    {
                        cached = -1;
                        Decrypt(index, chunk);
                        cached = index;
                    }
                    var within = (int)(offset + done - index * ChunkSize);
                    var take = Math.Min(total - done, ChunkLength(index) - within);
                    chunk.AsSpan(within, take).CopyTo(buffer[done..]);
                    done += take;
                }
            }
            return total;
        }

        int ChunkLength(long index) => (int)Math.Min(ChunkSize, Length - index * ChunkSize);

        /// <summary>Called under the gate.</summary>
        void Decrypt(long index, Span<byte> plain)
        {
            var length = ChunkLength(index);
            var sealedBytes = (sealedChunk ??= new byte[Math.Min(ChunkSize, Length)]).AsSpan(0, length);
            Span<byte> tag = stackalloc byte[TagSize];
            if (RandomAccess.Read(file, sealedBytes, index * ChunkSize) != length || RandomAccess.Read(file, tag, TagOffset(index)) != TagSize)
            {
                throw new CacheDamagedException(name);
            }
            Span<byte> chunkNonce = stackalloc byte[NonceSize];
            Span<byte> data = stackalloc byte[associated.Length + 20];
            try
            {
                cipher.aes.Value!.Decrypt(Nonce(index, chunkNonce), sealedBytes, tag, plain[..length], Associated(index, data));
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new CacheDamagedException(name);
            }
        }

        /// <summary>The file's nonce with the chunk number in its last 4 bytes, in <paramref name="result"/>.</summary>
        Span<byte> Nonce(long index, Span<byte> result)
        {
            nonce.CopyTo(result);
            BinaryPrimitives.WriteUInt32BigEndian(result[(NonceSize - 4)..], BinaryPrimitives.ReadUInt32BigEndian(nonce.AsSpan(NonceSize - 4)) ^ (uint)index);
            return result;
        }

        /// <summary>"name|length|index" in UTF-8, in <paramref name="result"/>.</summary>
        Span<byte> Associated(long index, Span<byte> result)
        {
            associated.CopyTo(result);
            index.TryFormat(result[associated.Length..], out var written, provider: System.Globalization.CultureInfo.InvariantCulture);
            return result[..(associated.Length + written)];
        }
    }
}

/// <summary>A cache file failed its integrity check, or isn't in the current format. It is dropped and downloaded again.</summary>
public sealed class CacheDamagedException(string name) : IOException($"cached content {name} is damaged; it is downloaded again on the next open");
