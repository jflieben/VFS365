using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace Vfs365.Core.Drive;

/// <summary>
/// Encrypts cached file content with AES-256 in counter mode. Each cache file has its own keystream (nonce from its name), so any
/// byte range can be written or read on its own, as the streaming download needs. A cache file only ever holds one version of
/// one item, so a keystream never covers different content.
/// </summary>
public sealed class CacheCipher
{
    readonly ThreadLocal<Aes> aes;

    public CacheCipher(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new ArgumentException("The cache key is 32 bytes", nameof(key));
        }
        var copy = key.ToArray();
        aes = new ThreadLocal<Aes>(() =>
        {
            var created = Aes.Create();
            created.Key = copy;
            return created;
        });
        KeyId = Convert.ToHexString(SHA256.HashData(copy))[..16];
    }

    /// <summary>Identifies the key without revealing it, so content written with another key is never read back as plaintext.</summary>
    public string KeyId { get; }

    public static byte[] NonceFor(string fileName) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fileName))[..8];

    /// <summary>Encrypts or decrypts (the same operation) <paramref name="data"/>, which sits at <paramref name="offset"/> in the file.</summary>
    public void Apply(Span<byte> data, long offset, ReadOnlySpan<byte> nonce)
    {
        if (data.IsEmpty)
        {
            return;
        }
        var first = offset / 16;
        var skip = (int)(offset % 16);
        var length = (skip + data.Length + 15) / 16 * 16;
        var counters = ArrayPool<byte>.Shared.Rent(length);
        var stream = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            for (var block = 0; block < length / 16; block++)
            {
                nonce[..8].CopyTo(counters.AsSpan(block * 16, 8));
                BinaryPrimitives.WriteInt64BigEndian(counters.AsSpan(block * 16 + 8, 8), first + block);
            }
            aes.Value!.EncryptEcb(counters.AsSpan(0, length), stream.AsSpan(0, length), PaddingMode.None);
            var keystream = stream.AsSpan(skip, data.Length);
            var i = 0;
            for (; i <= data.Length - Vector<byte>.Count; i += Vector<byte>.Count)
            {
                (new Vector<byte>(data[i..]) ^ new Vector<byte>(keystream[i..])).CopyTo(data[i..]);
            }
            for (; i < data.Length; i++)
            {
                data[i] ^= keystream[i];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(counters);
            ArrayPool<byte>.Shared.Return(stream);
        }
    }
}
