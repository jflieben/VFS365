using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vfs365.Agent;
using Vfs365.Core.Drive;

namespace Vfs365.Tests;

/// <summary>
/// The folder listings kept between runs (metadata.bin): sealed chunk by chunk as they are written, read back only when every
/// chunk is there, unchanged and in place.
/// </summary>
public sealed class MetadataStoreTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly byte[] key = RandomNumberGenerator.GetBytes(32);
    const int Header = 8 + 16, Chunk = SealedStream.ChunkSize + 16;

    string File1 => Path.Combine(directory, "metadata.bin");

    public MetadataStoreTests() => Directory.CreateDirectory(directory);

    static List<DriveSnapshot> Snapshots(int folders, int items)
    {
        var at = new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);
        return
        [
            new DriveSnapshot("b!drive-one", "https://graph.example/delta?token=abc", at,
                [.. Enumerable.Range(0, folders).Select(f => new FolderSnapshot($"Projects/folder-{f:D4}", $"folder-id-{f}",
                    [.. Enumerable.Range(0, items).Select(i => new DriveItemInfo($"01ITEM{f:D4}{i:D5}", $"Quarterly report {f}-{i}.docx", false, 1000 + i, at, at.AddMinutes(i),
                        Tag("", f, i, 3), Tag("c:", f, i, 5), ModifiedBy: "Someone"))],
                    at.AddHours(-1), "parent-id"))],
                [new AncestorSnapshot("Projects", "projects-id", "root-id")]),
            new DriveSnapshot("b!drive-two", "https://graph.example/delta?token=def", at, [new FolderSnapshot("", "root-two", [], at)]),
        ];
    }

    /// <summary>A tag in Graph's form: "{guid},version".</summary>
    static string Tag(string prefix, int folder, int item, int version) => "\"" + prefix + "{11111111-2222-3333-4444-" + $"{folder:D4}{item:D8}" + "}," + version + "\"";

    /// <summary>The size on disk of this much content: a header, and a tag per chunk (an empty stream has one chunk too).</summary>
    static long Stored(long length) => Header + length + Math.Max(1, (length + SealedStream.ChunkSize - 1) / SealedStream.ChunkSize) * 16;

    static string Json(IReadOnlyList<DriveSnapshot> snapshots) => JsonSerializer.Serialize(snapshots);

    /// <summary>A snapshot of several chunks, saved; its plain length.</summary>
    (List<DriveSnapshot> Snapshots, int Length) SaveSeveralChunks()
    {
        var snapshots = Snapshots(20, 100);
        MetadataStore.Save(snapshots, key, File1);
        var length = Encoding.UTF8.GetByteCount(Json(snapshots));
        Assert.True(length > 4 * SealedStream.ChunkSize);
        Assert.Equal(Stored(length), new FileInfo(File1).Length);
        return (snapshots, length);
    }

    void Change(Func<byte[], byte[]> change) => File.WriteAllBytes(File1, change(File.ReadAllBytes(File1)));

    [Fact]
    public void A_snapshot_reads_back_as_saved_and_is_not_plain_on_disk()
    {
        var snapshots = Snapshots(3, 40);
        MetadataStore.Save(snapshots, key, File1);

        Assert.Equal(Json(snapshots), Json(MetadataStore.Load(key, File1)));
        var stored = File.ReadAllBytes(File1);
        Assert.True(SealedStream.HasMarker(stored));
        Assert.True(stored.AsSpan().IndexOf("Quarterly"u8) < 0);
        Assert.True(stored.AsSpan().IndexOf("drive-one"u8) < 0);
        Assert.False(File.Exists(File1 + ".tmp"));
    }

    [Fact]
    public void A_save_replaces_the_file_and_each_one_is_sealed_differently()
    {
        MetadataStore.Save(Snapshots(2, 10), key, File1);
        var first = File.ReadAllBytes(File1);
        MetadataStore.Save(Snapshots(2, 10), key, File1);
        var second = File.ReadAllBytes(File1);

        Assert.Equal(first.Length, second.Length);
        Assert.NotEqual(first[Header..(Header + 64)], second[Header..(Header + 64)]); // a key of its own per file
        Assert.Equal(2, MetadataStore.Load(key, File1).Count);
        Assert.Empty(MetadataStore.Load(key, Path.Combine(directory, "missing.bin")));
        Assert.Empty(MetadataStore.Load(RandomNumberGenerator.GetBytes(32), File1)); // another user's key
    }

    [Fact]
    public void A_changed_byte_anywhere_discards_the_snapshot()
    {
        var (snapshots, _) = SaveSeveralChunks();
        var good = File.ReadAllBytes(File1);
        Assert.Equal(Json(snapshots), Json(MetadataStore.Load(key, File1)));

        // The marker, the salt, the first chunk, a chunk in the middle, a tag, the last chunk and its tag
        foreach (var at in new[] { 0, 10, Header + 5, Header + 2 * Chunk + 100, Header + Chunk - 1, good.Length - 20, good.Length - 1 })
        {
            Change(_ => { var bytes = good.ToArray(); bytes[at] ^= 0x40; return bytes; });
            Assert.Empty(MetadataStore.Load(key, File1));
        }
    }

    [Fact]
    public void A_file_that_was_cut_off_discards_the_snapshot()
    {
        SaveSeveralChunks();
        var good = File.ReadAllBytes(File1);

        // In the last chunk, in a chunk before it, right after a chunk, and down to the header or less
        foreach (var length in new[] { good.Length - 1, good.Length - 16, Header + 2 * Chunk + 500, Header + 3 * Chunk, Header + Chunk, Header, 10, 0 })
        {
            Change(_ => good[..length]);
            Assert.Empty(MetadataStore.Load(key, File1));
        }
    }

    [Fact]
    public void Chunks_swapped_left_out_repeated_or_added_discard_the_snapshot()
    {
        SaveSeveralChunks();
        var good = File.ReadAllBytes(File1);
        byte[] ChunkAt(int index) => good[(Header + index * Chunk)..Math.Min(good.Length, Header + (index + 1) * Chunk)];
        var chunks = Enumerable.Range(0, (good.Length - Header + Chunk - 1) / Chunk).Select(ChunkAt).ToList();
        byte[] Build(IEnumerable<byte[]> order) => [.. good[..Header], .. order.SelectMany(c => c)];
        Assert.Equal(good, Build(chunks));
        var last = chunks.Count - 1;

        var changes = new List<IEnumerable<byte[]>>
        {
            chunks.Select((c, i) => i == 0 ? chunks[1] : i == 1 ? chunks[0] : c), // two swapped
            chunks.Where((_, i) => i != last), // the last one left out: the rest is whole chunks
            chunks.Where((_, i) => i != 1), // one in the middle left out
            chunks.Where((_, i) => i != 0), // the first left out
            chunks.Take(2).Append(chunks[1]).Concat(chunks.Skip(2)), // one twice
            chunks.Append(chunks[last]), // the last one twice
            chunks.Append(new byte[40]), // bytes after the last
            chunks.Take(1), // only the first
        };
        foreach (var order in changes)
        {
            Change(_ => Build(order));
            Assert.Empty(MetadataStore.Load(key, File1));
        }

        Change(_ => good);
        Assert.Equal(2, MetadataStore.Load(key, File1).Count);
    }

    [Fact]
    public void Chunks_of_another_file_under_the_same_key_are_refused()
    {
        var (snapshots, _) = SaveSeveralChunks();
        var first = File.ReadAllBytes(File1);
        MetadataStore.Save(snapshots, key, File1); // the same content, sealed again
        var second = File.ReadAllBytes(File1);

        Change(_ => [.. second[..(Header + Chunk)], .. first[(Header + Chunk)..]]);

        Assert.Empty(MetadataStore.Load(key, File1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(SealedStream.ChunkSize - 1)]
    [InlineData(SealedStream.ChunkSize)]
    [InlineData(SealedStream.ChunkSize + 1)]
    [InlineData(3 * SealedStream.ChunkSize)]
    [InlineData(3 * SealedStream.ChunkSize + 777)]
    public void Content_of_any_length_reads_back(int length)
    {
        var content = RandomNumberGenerator.GetBytes(length);
        var stored = new MemoryStream();
        using (var writer = SealedStream.Create(stored, key))
        {
            // In pieces that don't line up with chunks
            for (var offset = 0; offset < length;)
            {
                var piece = Math.Min(length - offset, 10_007);
                writer.Write(content, offset, piece);
                offset += piece;
            }
            writer.Complete();
        }
        Assert.Equal(Stored(length), stored.Length);

        var read = new MemoryStream();
        using (var reader = SealedStream.Open(new MemoryStream(stored.ToArray()), key))
        {
            reader.CopyTo(read, 5000);
        }
        Assert.Equal(content, read.ToArray());
    }

    [Fact]
    public void A_file_whose_writer_never_finished_does_not_read_to_an_end()
    {
        // As after a failure halfway: the chunks written so far are genuine, the last one is missing
        var stored = new MemoryStream();
        using (var writer = SealedStream.Create(stored, key))
        {
            writer.Write(new byte[3 * SealedStream.ChunkSize + 100]);
        }
        Assert.Equal(Header + 3 * Chunk, stored.Length);

        using var reader = SealedStream.Open(new MemoryStream(stored.ToArray()), key);
        var buffer = new byte[SealedStream.ChunkSize];
        Assert.Equal(SealedStream.ChunkSize, reader.ReadAtLeast(buffer, buffer.Length)); // chunks before the end do read
        Assert.Equal(SealedStream.ChunkSize, reader.ReadAtLeast(buffer, buffer.Length));
        Assert.Throws<InvalidDataException>(() => reader.ReadAtLeast(buffer, buffer.Length));
        Assert.Throws<InvalidDataException>(() => SealedStream.Open(new MemoryStream(new byte[10]), key));
        Assert.Throws<InvalidDataException>(() => SealedStream.Open(new MemoryStream(new byte[100]), key));
    }

    /// <summary>Counts what passes and the largest piece asked for at once.</summary>
    sealed class Watched(Stream inner) : Stream
    {
        public int Largest;
        public long Total;

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            (Largest, Total) = (Math.Max(Largest, buffer.Length), Total + buffer.Length);
            inner.Write(buffer);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            Largest = Math.Max(Largest, buffer.Length);
            var read = inner.Read(buffer);
            Total += read;
            return read;
        }

        public override void Flush() => inner.Flush();
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public void A_large_snapshot_is_written_and_read_a_chunk_at_a_time()
    {
        var snapshots = Snapshots(400, 100); // 40,000 items
        var length = Encoding.UTF8.GetByteCount(Json(snapshots));
        Assert.True(length > 8 << 20);
        const int piece = 1 << 20; // far below the whole, with room for the serializer's own buffer

        // Writing: the serializer hands over pieces, the file gets a chunk at a time
        using var file = new FileStream(File1, FileMode.Create, FileAccess.ReadWrite);
        var stored = new Watched(file);
        using (var writer = SealedStream.Create(stored, key))
        {
            var plain = new Watched(writer);
            JsonSerializer.Serialize(plain, snapshots);
            writer.Complete();
            Assert.Equal(length, plain.Total);
            Assert.InRange(plain.Largest, 1, piece);
        }
        Assert.Equal(Chunk, stored.Largest);
        Assert.Equal(Stored(length), stored.Total);

        // Reading: a chunk at a time from the file, in pieces to the deserializer
        file.Position = 0;
        var source = new Watched(file);
        using (var reader = SealedStream.Open(source, key))
        {
            var plain = new Watched(reader);
            var loaded = JsonSerializer.Deserialize<List<DriveSnapshot>>(plain)!;
            Assert.Equal(length, plain.Total);
            Assert.InRange(plain.Largest, 1, piece);
            Assert.Equal(400, loaded[0].Folders.Count);
            Assert.Equal("Quarterly report 399-99.docx", loaded[0].Folders[399].Items[99].Name);
        }
        Assert.Equal(Chunk, source.Largest);
        file.Dispose();

        // The store itself: what a save allocates doesn't grow with the snapshot
        MetadataStore.Save(Snapshots(1, 1), key, File1); // warm-up: serializer metadata, pooled buffers
        var before = GC.GetAllocatedBytesForCurrentThread();
        MetadataStore.Save(snapshots, key, File1);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.InRange(allocated, 0, length / 4);
        Assert.Equal(Json(snapshots), Json(MetadataStore.Load(key, File1)));
    }

    [Fact]
    public void A_file_from_before_0_9_0_still_loads_and_the_next_save_replaces_it()
    {
        // 0.8.x: the whole JSON sealed in one piece with the cache key itself: nonce (12) | tag (16) | ciphertext
        var snapshots = Snapshots(3, 40);
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshots);
        var old = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(old.AsSpan(0, 12));
        using (var gcm = new AesGcm(key, 16))
        {
            gcm.Encrypt(old.AsSpan(0, 12), plain, old.AsSpan(28), old.AsSpan(12, 16));
        }
        File.WriteAllBytes(File1, old);

        Assert.Equal(Json(snapshots), Json(MetadataStore.Load(key, File1)));
        Assert.Empty(MetadataStore.Load(RandomNumberGenerator.GetBytes(32), File1));

        MetadataStore.Save(snapshots, key, File1);
        Assert.True(SealedStream.HasMarker(File.ReadAllBytes(File1)));
        Assert.Equal(Json(snapshots), Json(MetadataStore.Load(key, File1)));

        old[^1] ^= 1; // a damaged old file is discarded like a damaged new one
        File.WriteAllBytes(File1, old);
        Assert.Empty(MetadataStore.Load(key, File1));
        File.WriteAllText(File1, "not a snapshot");
        Assert.Empty(MetadataStore.Load(key, File1));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
