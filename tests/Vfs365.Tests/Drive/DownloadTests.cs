using System.Collections.Concurrent;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Downloads that fail now and then are tried again quietly; content that isn't the listed length is never served cut off.</summary>
public sealed class DownloadTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly ConcurrentQueue<string> log = new();
    readonly ConcurrentQueue<EngineChange> changes = new();
    readonly DriveEngine engine;

    public DownloadTests()
    {
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("b!x"));
        engine = new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), Log = log.Enqueue, Changed = changes.Enqueue });
    }

    static byte[] Data(int length, int seed = 365)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    async Task<byte[]> ReadAllAsync(string path)
    {
        var file = (await engine.GetEntryAsync(path, default))!;
        using var source = await engine.OpenContentAsync(file, default);
        var copy = new MemoryStream();
        var buffer = new byte[64 * 1024];
        for (long offset = 0; ; offset += buffer.Length)
        {
            var read = await source.ReadAsync(offset, buffer, default);
            if (read == 0)
            {
                return copy.ToArray();
            }
            copy.Write(buffer, 0, read);
        }
    }

    [Fact]
    public async Task A_dropped_download_is_tried_again_and_the_read_succeeds()
    {
        var data = Data(3 * 1024 * 1024 + 10); // four 1 MiB blocks
        api.Add("report.pdf", data);
        api.FailDownloads = 2;

        Assert.Equal(data, await ReadAllAsync("\\OneDrive\\report.pdf"));
        Assert.Equal(3, api.Downloads);
    }

    [Fact]
    public async Task Content_shorter_than_listed_fails_once_and_then_reads_whole()
    {
        api.Add("plan.docx", Data(200_000));
        var file = (await engine.GetEntryAsync("\\OneDrive\\plan.docx", default))!;
        var shorter = Data(150_000, seed: 7);
        api.ServeOtherContent("plan.docx", shorter);

        using (var source = await engine.OpenContentAsync(file, default))
        {
            await Assert.ThrowsAsync<ContentChangedException>(() => source.ReadAsync(0, new byte[4096], default).AsTask());
        }
        Assert.Equal(1, api.Downloads); // no pointless retries
        Assert.Contains(changes, c => c.Path == "\\OneDrive\\plan.docx" && c.Kind == ChangeKind.Modified);
        Assert.Contains(log, line => line.Contains("plan.docx") && line.Contains("200000 bytes listed, 150000 served"));

        Assert.Equal(150_000, (await engine.GetEntryAsync("\\OneDrive\\plan.docx", default))!.Size);
        Assert.Equal(shorter, await ReadAllAsync("\\OneDrive\\plan.docx"));
    }

    [Fact]
    public async Task Content_longer_than_listed_is_never_served_cut_off()
    {
        api.Add("budget.xlsx", Data(100_000));
        var file = (await engine.GetEntryAsync("\\OneDrive\\budget.xlsx", default))!;
        var longer = Data(130_000, seed: 8);
        api.ServeOtherContent("budget.xlsx", longer);

        using (var source = await engine.OpenContentAsync(file, default))
        {
            await Assert.ThrowsAsync<ContentChangedException>(() => source.ReadAsync(0, new byte[4096], default).AsTask());
        }

        Assert.Equal(longer, await ReadAllAsync("\\OneDrive\\budget.xlsx"));
    }

    [Fact]
    public async Task A_same_size_change_during_a_download_is_never_mixed_into_it()
    {
        const int MiB = 1024 * 1024;
        var original = Data(40 * MiB);
        api.Add("data.accdb", original);
        var file = (await engine.GetEntryAsync("\\OneDrive\\data.accdb", default))!;
        var edited = Data(40 * MiB, seed: 9);

        using (var source = await engine.OpenContentAsync(file, default))
        {
            var head = new byte[4096];
            await source.ReadAsync(0, head, default);
            Assert.Equal(original.AsSpan(0, 4096).ToArray(), head);
            api.RemoteEdit("data.accdb", edited);
            await Assert.ThrowsAsync<ContentChangedException>(() => source.ReadAsync(30 * MiB, new byte[4096], default).AsTask());
        }

        Assert.Contains(log, line => line.Contains("data.accdb") && line.Contains("another version is served"));
        Assert.Equal(edited, await ReadAllAsync("\\OneDrive\\data.accdb"));
    }

    [Fact]
    public async Task A_download_that_gave_up_is_started_afresh_by_the_next_open()
    {
        var data = Data(100_000);
        api.Add("notes.txt", data);
        api.FailDownloads = 3;
        var file = (await engine.GetEntryAsync("\\OneDrive\\notes.txt", default))!;

        using var broken = await engine.OpenContentAsync(file, default);
        await Assert.ThrowsAsync<IOException>(() => broken.ReadAsync(0, new byte[4096], default).AsTask());
        // The first handle is still open: a new one doesn't inherit its failure
        using var fresh = await engine.OpenContentAsync(file, default);
        var buffer = new byte[data.Length];
        var read = await fresh.ReadAsync(0, buffer, default);

        Assert.Equal(data.Length, read);
        Assert.Equal(data, buffer);
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
