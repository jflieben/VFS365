using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Reads don't wait for whole downloads, and large copies onto the drive upload while they are written.</summary>
public sealed class StreamingTests : IDisposable
{
    const int MiB = 1024 * 1024;
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly DriveEngine engine;

    public StreamingTests()
    {
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("b!x"));
        engine = new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });
    }

    static byte[] Data(int length)
    {
        var bytes = new byte[length];
        new Random(365).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public async Task First_read_of_a_large_file_does_not_wait_for_all_of_it()
    {
        var data = Data(40 * MiB);
        api.Add("big.bin", data);
        var file = (await engine.GetEntryAsync("\\OneDrive\\big.bin", default))!;

        using var source = await engine.OpenContentAsync(file, default);
        var head = new byte[4096];
        await source.ReadAsync(0, head, default);
        var tail = new byte[4096];
        await source.ReadAsync(38 * MiB, tail, default);

        Assert.Equal(data.AsSpan(0, 4096).ToArray(), head);
        Assert.Equal(data.AsSpan(38 * MiB, 4096).ToArray(), tail);
        Assert.True(api.BytesStreamed < 25 * MiB, $"pulled {api.BytesStreamed / MiB} MiB for two small reads");
        Assert.Equal(2, api.Downloads);
    }

    [Fact]
    public async Task Sequential_read_gets_everything_and_completes_the_cache()
    {
        var data = Data(10 * MiB + 123);
        api.Add("big.bin", data);
        var file = (await engine.GetEntryAsync("\\OneDrive\\big.bin", default))!;

        var copy = new MemoryStream();
        using (var source = await engine.OpenContentAsync(file, default))
        {
            var buffer = new byte[MiB];
            for (long offset = 0; ; offset += buffer.Length)
            {
                var read = await source.ReadAsync(offset, buffer, default);
                if (read == 0)
                {
                    break;
                }
                copy.Write(buffer, 0, read);
            }
        }
        using var cached = await engine.OpenContentAsync(file, default);

        Assert.Equal(data, copy.ToArray());
        Assert.Equal(1, api.Downloads);
        Assert.Equal(data.Length, cached.Length);
    }

    async Task<FsEntry> CopyInAsync(string name, byte[] data, bool presize, int chunk = MiB, bool outOfOrder = false, bool patchHeader = false)
    {
        var path = "\\OneDrive\\" + name;
        var entry = await engine.CreateAsync(path, false, default);
        using var staging = File.OpenHandle(entry.StagingPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        if (presize)
        {
            RandomAccess.SetLength(staging, data.Length);
            engine.SizeSet(entry, data.Length);
        }
        var offsets = Enumerable.Range(0, (data.Length + chunk - 1) / chunk).Select(i => (long)i * chunk).ToList();
        if (outOfOrder)
        {
            (offsets[3], offsets[^1]) = (offsets[^1], offsets[3]); // a gap filled later: still streams
        }
        if (patchHeader)
        {
            offsets.Add(0); // rewrites bytes already sent: falls back to upload on close
        }
        foreach (var offset in offsets)
        {
            var length = (int)Math.Min(chunk, data.Length - offset);
            RandomAccess.Write(staging, data.AsSpan((int)offset, length), offset);
            engine.MarkDirty(entry);
            await engine.WrittenAsync(entry, offset, length, default);
        }
        return entry;
    }

    [Fact]
    public async Task Presized_copy_uploads_while_it_is_written()
    {
        var data = Data(23 * MiB);

        await CopyInAsync("copy.bin", data, presize: true);
        var beforeClose = api.Fragments;
        await engine.CommitAsync("\\OneDrive\\copy.bin", default);

        Assert.Equal(1, api.Sessions);
        Assert.True(beforeClose >= 3, $"{beforeClose} fragments before close");
        Assert.Equal(5, api.Fragments);
        Assert.Equal(data, Bytes("copy.bin"));
        Assert.False(Assert.Single(api.Uploads).Whole);
    }

    [Fact]
    public async Task Gaps_filled_later_still_stream()
    {
        var data = Data(23 * MiB);

        await CopyInAsync("copy.bin", data, presize: true, outOfOrder: true);
        await engine.CommitAsync("\\OneDrive\\copy.bin", default);

        Assert.Equal(data, Bytes("copy.bin"));
        Assert.False(Assert.Single(api.Uploads).Whole);
    }

    [Fact]
    public async Task Rewriting_sent_bytes_falls_back_to_upload_on_close()
    {
        var data = Data(23 * MiB);

        await CopyInAsync("copy.bin", data, presize: true, patchHeader: true);
        await engine.CommitAsync("\\OneDrive\\copy.bin", default);

        Assert.Equal(data, Bytes("copy.bin"));
        Assert.True(Assert.Single(api.Uploads).Whole);
        Assert.Equal(1, api.CancelledSessions);
    }

    [Fact]
    public async Task Without_a_size_first_it_uploads_on_close()
    {
        var data = Data(12 * MiB);

        await CopyInAsync("copy.bin", data, presize: false);
        Assert.Equal(0, api.Sessions);
        await engine.CommitAsync("\\OneDrive\\copy.bin", default);

        Assert.Equal(data, Bytes("copy.bin"));
        Assert.True(Assert.Single(api.Uploads).Whole);
    }

    [Fact]
    public async Task Replacing_a_large_file_streams_into_the_same_item()
    {
        var original = api.Add("copy.bin", Data(MiB));
        var data = Data(18 * MiB);
        var entry = (await engine.GetEntryAsync("\\OneDrive\\copy.bin", default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: true, default);
        entry = (await engine.GetEntryAsync("\\OneDrive\\copy.bin", default))!;
        await File.WriteAllBytesAsync(staging, data);
        engine.SizeSet(entry, data.Length);
        for (long offset = 0; offset < data.Length; offset += MiB)
        {
            await engine.WrittenAsync(entry, offset, MiB, default);
        }
        await engine.CommitAsync("\\OneDrive\\copy.bin", default);

        var upload = Assert.Single(api.Uploads);
        Assert.Equal(original.Id, upload.ItemId);
        Assert.False(upload.Whole);
        Assert.Equal(data, Bytes("copy.bin"));
    }

    byte[] Bytes(string path) => api.BytesOf(path)!;

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
