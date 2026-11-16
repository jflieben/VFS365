using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Vfs365.Agent;

/// <summary>
/// A file sealed with AES-256-GCM in 64 KiB chunks, written and read as a stream with one chunk in memory. Layout: a marker (8),
/// a random salt (16), then per chunk its ciphertext and tag (16). Every chunk but the last holds ChunkSize bytes; the last holds
/// what is left (nothing, for an empty stream). The key is derived from the cache key and the salt (HKDF), so every file has its own
/// and a chunk's nonce is its number. Whether a chunk is the last is authenticated with it. So chunks that were changed, swapped,
/// left out or cut off (the last too) fail to read: <see cref="Reader"/> throws <see cref="InvalidDataException"/> and reports the
/// end only after the last chunk checked out.
/// </summary>
static class SealedStream
{
    public const int ChunkSize = 64 * 1024;
    const int TagSize = 16, SaltSize = 16, NonceSize = 12;
    static readonly byte[] Marker = "VFS365S1"u8.ToArray();
    static readonly byte[] Purpose = "vfs365 sealed stream"u8.ToArray();

    /// <summary>The file starts as one written here does (an older metadata.bin starts with a random nonce).</summary>
    public static bool HasMarker(ReadOnlySpan<byte> start) => start.StartsWith(Marker);

    /// <summary>Writes the header; what is written to the result is sealed into <paramref name="output"/>. Call <see cref="Writer.Complete"/> at the end.</summary>
    public static Writer Create(Stream output, byte[] key)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        output.Write(Marker);
        output.Write(salt);
        return new Writer(output, Derive(key, salt));
    }

    /// <summary>Reads the header; the result gives the content. Throws <see cref="InvalidDataException"/> when it isn't a file written here.</summary>
    public static Reader Open(Stream input, byte[] key)
    {
        var header = new byte[Marker.Length + SaltSize];
        if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length || !HasMarker(header))
        {
            throw new InvalidDataException("not a sealed file");
        }
        return new Reader(input, Derive(key, header.AsSpan(Marker.Length).ToArray()));
    }

    static AesGcm Derive(byte[] key, byte[] salt) => new(HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, salt, Purpose), TagSize);

    /// <summary>The chunk's number as its nonce: unique under the file's own key.</summary>
    static ReadOnlySpan<byte> Nonce(long index, Span<byte> nonce)
    {
        nonce.Clear();
        BinaryPrimitives.WriteInt64BigEndian(nonce[(NonceSize - 8)..], index);
        return nonce;
    }

    /// <summary>What is authenticated with each chunk: whether it is the last.</summary>
    static readonly byte[] Last = [1], More = [0];

    public sealed class Writer : Stream
    {
        readonly Stream output;
        readonly AesGcm gcm;
        readonly byte[] plain = new byte[ChunkSize], sealedChunk = new byte[ChunkSize + TagSize];
        int filled;
        long index;
        bool completed;

        internal Writer(Stream output, AesGcm gcm) => (this.output, this.gcm) = (output, gcm);

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            ObjectDisposedException.ThrowIf(completed, this);
            while (buffer.Length > 0)
            {
                if (filled == ChunkSize)
                {
                    Seal(last: false); // a full chunk goes only when more follows: the last one is sealed as such
                }
                var take = Math.Min(buffer.Length, ChunkSize - filled);
                buffer[..take].CopyTo(plain.AsSpan(filled));
                filled += take;
                buffer = buffer[take..];
            }
        }

        /// <summary>Seals what is left as the last chunk. A file without it (the writer failed halfway) doesn't read.</summary>
        public void Complete()
        {
            ObjectDisposedException.ThrowIf(completed, this);
            Seal(last: true);
            completed = true;
            output.Flush();
        }

        void Seal(bool last)
        {
            Span<byte> nonce = stackalloc byte[NonceSize];
            gcm.Encrypt(Nonce(index++, nonce), plain.AsSpan(0, filled), sealedChunk.AsSpan(0, filled), sealedChunk.AsSpan(filled, TagSize), last ? Last : More);
            output.Write(sealedChunk, 0, filled + TagSize);
            filled = 0;
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Flush() { } // chunks go when they are full, the last with Complete
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                gcm.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    public sealed class Reader : Stream
    {
        readonly Stream input;
        readonly AesGcm gcm;
        readonly byte[] plain = new byte[ChunkSize];

        /// <summary>The sealed chunk to open next, and the buffer the one after it is read into: only the last is followed by nothing.</summary>
        byte[] current = new byte[ChunkSize + TagSize], following = new byte[ChunkSize + TagSize];
        int currentLength, available, position;
        long index;
        bool ended;

        internal Reader(Stream input, AesGcm gcm)
        {
            (this.input, this.gcm) = (input, gcm);
            currentLength = input.ReadAtLeast(current, current.Length, throwOnEndOfStream: false);
        }

        public override int Read(Span<byte> buffer)
        {
            while (position == available)
            {
                if (ended)
                {
                    return 0;
                }
                OpenNext();
            }
            var take = Math.Min(buffer.Length, available - position);
            plain.AsSpan(position, take).CopyTo(buffer);
            position += take;
            return take;
        }

        void OpenNext()
        {
            var followingLength = input.ReadAtLeast(following, following.Length, throwOnEndOfStream: false);
            var last = followingLength == 0;
            if (currentLength < TagSize || (!last && currentLength != current.Length))
            {
                throw new InvalidDataException("the sealed file is cut off");
            }
            var length = currentLength - TagSize;
            Span<byte> nonce = stackalloc byte[NonceSize];
            try
            {
                gcm.Decrypt(Nonce(index++, nonce), current.AsSpan(0, length), current.AsSpan(length, TagSize), plain.AsSpan(0, length), last ? Last : More);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException("the sealed file was changed, is incomplete or was sealed with another key");
            }
            (available, position, ended) = (length, 0, last);
            (current, following, currentLength) = (following, current, followingLength);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                gcm.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
