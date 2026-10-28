using System.Diagnostics;
using System.Security.Cryptography;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Xunit.Abstractions;

namespace Vfs365.Tests.Drive;

/// <summary>Timings: run on their own, so they neither wait behind other tests nor slow down other tests' timings.</summary>
[CollectionDefinition(nameof(WriteGateTests), DisableParallelization = true)]
public sealed class WriteGateCollection;

/// <summary>
/// Long work on one file never holds the write gate, which every create, delete, rename and close of every other file waits for:
/// probes create unrelated files while it runs, and their longest wait is the longest the gate was held meanwhile.
/// </summary>
[Collection(nameof(WriteGateTests))]
public sealed class WriteGateTests(ITestOutputHelper output) : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    DateTimeOffset now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";
    const int MiB = 1024 * 1024;

    /// <summary>What a probe may wait at most: half the 500 ms operations that holding the gate would make it wait, so busy CI runners pass.</summary>
    static readonly TimeSpan Brief = TimeSpan.FromMilliseconds(250);

    DriveEngine Create(bool background = false, bool encrypted = false) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api,
                new ContentCacheOptions { MaxBytes = 4L << 30, Cipher = encrypted ? new CacheCipher(RandomNumberGenerator.GetBytes(32)) : null }),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                UploadNewFilesInBackground = background,
            });

    /// <summary>Creates Office lock files (never uploaded) one after another until stopped; reports the longest create.</summary>
    sealed class Probe
    {
        readonly CancellationTokenSource stop = new();
        readonly Task<TimeSpan> run;
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly List<TimeSpan> finished = [];

        /// <summary>The probe's clock, which <see cref="Finished"/> is on.</summary>
        public TimeSpan Now => clock.Elapsed;

        /// <summary>When each create finished; read after <see cref="StopAsync"/>.</summary>
        public IReadOnlyList<TimeSpan> Finished => finished;

        public Probe(DriveEngine engine)
        {
            run = Task.Run(async () =>
            {
                var longest = TimeSpan.Zero;
                for (var i = 0; !stop.IsCancellationRequested; i++)
                {
                    var watch = Stopwatch.StartNew();
                    await engine.CreateAsync($"{Root}~$probe{i}.docx", false, default);
                    longest = watch.Elapsed > longest ? watch.Elapsed : longest;
                    finished.Add(clock.Elapsed);
                    await Task.Delay(2);
                }
                return longest;
            });
        }

        public async Task<TimeSpan> StopAsync()
        {
            stop.Cancel();
            return await run;
        }
    }

    async Task<DriveEngine> ReadyAsync(bool background = false, bool encrypted = false)
    {
        var engine = Create(background, encrypted);
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        return engine;
    }

    void Report(string what, TimeSpan time) => output.WriteLine($"{what}: {time.TotalMilliseconds:0} ms");

    /// <summary>
    /// Runs <paramref name="milliseconds"/> of slow network work and notes when, on the probe's clock: held under the gate, no create
    /// could finish meanwhile. A busy runner (CI) only makes each create slower, so tests count those creates instead of timing them.
    /// </summary>
    sealed class SlowCall(Probe probe, int milliseconds)
    {
        public TimeSpan? From { get; private set; }
        public TimeSpan? To { get; private set; }

        public async Task RunAsync()
        {
            From = probe.Now;
            await Slow(milliseconds);
            To = probe.Now;
        }

        /// <summary>Creates that finished during the call; the first 20 ms are left out (a create that passed the gate just before the call took it may end then).</summary>
        public int Finished => From is { } from && To is { } to ? probe.Finished.Count(t => t > from + TimeSpan.FromMilliseconds(20) && t < to) : 0;
    }

    static Task Slow(int milliseconds) => Task.Delay(milliseconds);

    [Fact]
    public async Task Caching_an_uploaded_file_holds_up_no_other_file()
    {
        api.Add("big.bin", content: "v1");
        var engine = await ReadyAsync(encrypted: true);
        var entry = await engine.StageAsync((await engine.GetEntryAsync(Root + "big.bin", default))!, truncate: true, default);
        var data = new byte[256 * MiB];
        new Random(365).NextBytes(data);
        await File.WriteAllBytesAsync(entry.StagingPath!, data);
        engine.MarkDirty(entry);

        var probe = new Probe(engine);
        // The adoption encrypts into a .part file in the cache: from its first to its last sight is when the cache adopted the file
        var cache = Path.Combine(directory, "cache");
        TimeSpan? adoptFrom = null, adoptTo = null;
        using var watching = new CancellationTokenSource();
        var watcher = Task.Run(async () =>
        {
            while (!watching.IsCancellationRequested)
            {
                try
                {
                    if (Directory.EnumerateFiles(cache, "*.part", SearchOption.AllDirectories).Any())
                    {
                        adoptFrom ??= probe.Now;
                        adoptTo = probe.Now;
                    }
                }
                catch (IOException)
                {
                }
                await Task.Delay(1);
            }
        });
        var watch = Stopwatch.StartNew();
        await engine.CommitAsync(Root + "big.bin", default);
        Report("close of a 256 MiB save", watch.Elapsed);
        var background = Stopwatch.StartNew();
        await engine.WaitForUploadsAsync();
        Report("upload and adopting into the encrypted cache", background.Elapsed);
        var longest = await probe.StopAsync();
        watching.Cancel();
        await watcher;
        Report("longest create meanwhile", longest);

        Assert.Equal(data, api.BytesOf("big.bin"));
        Assert.True(adoptFrom is not null, "the cache's adoption (its .part file) was never seen");
        var adoption = adoptTo!.Value - adoptFrom.Value;
        var during = probe.Finished.Count(t => t > adoptFrom && t < adoptTo);
        Report($"adoption ({during} creates finished during it)", adoption);
        // Held under the gate, no create could finish while the cache adopted the file. A busy runner (CI) only makes each create slower,
        // so this counts finished creates instead of comparing times (a create once took 673 ms of a 2,185 ms upload there)
        Assert.True(during > 0, $"no create finished during the {adoption.TotalMilliseconds:0} ms adoption (longest create {longest.TotalMilliseconds:0} ms)");
        using var source = await engine.OpenContentAsync((await engine.GetEntryAsync(Root + "big.bin", default))!, default);
        Assert.Equal(data.Length, source.Length);
        Assert.Equal(0, api.Downloads);
    }

    [Fact]
    public async Task A_create_at_a_deleted_name_waits_only_for_that_delete()
    {
        for (var i = 0; i < 8; i++)
        {
            api.Add($"f{i}.txt", content: $"{i}");
        }
        var engine = await ReadyAsync();
        for (var i = 0; i < 8; i++)
        {
            await engine.DeleteAsync(Root + $"f{i}.txt", default);
        }
        api.BeforeDelete = () => Slow(500);
        now += TimeSpan.FromMinutes(1);
        await engine.ProcessDueAsync(false, default);

        var watch = Stopwatch.StartNew();
        await engine.CreateAsync(Root + "f7.txt", false, default);
        var waited = watch.Elapsed;
        Report("create at the name of the last of 8 due deletes (500 ms each, 4 at a time)", waited);
        await engine.WaitForUploadsAsync();

        Assert.True(waited < TimeSpan.FromMilliseconds(850), $"the create waited {waited.TotalMilliseconds:0} ms"); // its own delete, not the round before it too
        Assert.Equal(8, api.Deletes);
    }

    [Fact]
    public async Task New_file_closes_flush_without_holding_the_gate()
    {
        var engine = await ReadyAsync(background: true);
        var network = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => network.Task;

        var watch = Stopwatch.StartNew();
        await Parallel.ForEachAsync(Enumerable.Range(0, 400), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            var entry = await engine.CreateAsync(Root + $"n{i}.txt", false, default);
            await File.WriteAllTextAsync(entry.StagingPath!, $"content {i}");
            engine.MarkDirty(entry);
            await engine.CommitAsync(Root + $"n{i}.txt", default);
        });
        var elapsed = watch.Elapsed;
        output.WriteLine($"400 new files created and closed from 8 threads: {elapsed.TotalMilliseconds:0} ms, {400 / elapsed.TotalSeconds:0} closes/s");
        network.SetResult();
        await engine.WaitForUploadsAsync();

        Assert.Equal(400, api.Uploads.Count);
    }

    [Fact]
    public async Task A_folder_rename_does_not_wait_for_the_uploads_below_it()
    {
        var engine = await ReadyAsync(background: true);
        await engine.CreateAsync(Root + "docs", true, default);
        api.BeforeUpload = () => Slow(200);
        for (var i = 0; i < 40; i++)
        {
            var entry = await engine.CreateAsync(Root + $"docs\\f{i}.txt", false, default);
            await File.WriteAllTextAsync(entry.StagingPath!, $"content {i}");
            engine.MarkDirty(entry);
            await engine.CommitAsync(Root + $"docs\\f{i}.txt", default);
        }

        var watch = Stopwatch.StartNew();
        await engine.RenameAsync(Root + "docs", Root + "archive", false, default);
        Report("rename of a folder with 40 new files uploading (200 ms each, 4 at a time)", watch.Elapsed);
        var renamed = watch.Elapsed;
        await engine.WaitForUploadsAsync();

        Assert.True(renamed < TimeSpan.FromMilliseconds(1000), $"the rename took {renamed.TotalMilliseconds:0} ms");
        Assert.Equal(["archive", .. Enumerable.Range(0, 40).Select(i => $"archive/f{i}.txt").Order()], api.Paths);
        Assert.Equal("content 7", api.ContentOf("archive/f7.txt"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_folder_delete_holds_up_no_other_file()
    {
        api.Add("old", folder: true);
        var engine = await ReadyAsync();
        var probe = new Probe(engine);
        var delete = new SlowCall(probe, 500);
        api.BeforeDelete = delete.RunAsync;

        await Task.Delay(20);
        await engine.DeleteAsync(Root + "old", default);
        var longest = await probe.StopAsync();
        Report($"longest create during a folder delete (500 ms, {delete.Finished} creates finished during it)", longest);

        Assert.True(delete.Finished > 0, $"no create finished during the folder delete (longest create {longest.TotalMilliseconds:0} ms)");
        Assert.Empty(api.Paths);
    }

    [Fact]
    public async Task A_folder_rename_holds_up_no_other_file()
    {
        api.Add("a", folder: true);
        api.Add("a/x.txt");
        var engine = await ReadyAsync();
        var probe = new Probe(engine);
        var move = new SlowCall(probe, 500);
        api.BeforeMove = move.RunAsync;

        await Task.Delay(20);
        await engine.RenameAsync(Root + "a", Root + "b", false, default);
        var longest = await probe.StopAsync();
        Report($"longest create during a folder rename (500 ms, {move.Finished} creates finished during it)", longest);

        Assert.True(move.Finished > 0, $"no create finished during the folder rename (longest create {longest.TotalMilliseconds:0} ms)");
        Assert.Equal(["b", "b/x.txt"], api.Paths);
    }

    [Fact]
    public async Task Stopping_a_streaming_upload_holds_up_no_other_file()
    {
        var engine = await ReadyAsync();
        var entry = await engine.CreateAsync(Root + "copy.bin", false, default);
        using (var staging = File.OpenHandle(entry.StagingPath!, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            RandomAccess.SetLength(staging, 23 * MiB);
            engine.SizeSet(entry, 23 * MiB);
            RandomAccess.Write(staging, new byte[6 * MiB], 0);
        }
        engine.MarkDirty(entry);
        api.BeforeFragment = _ => Slow(500);
        await engine.WrittenAsync(entry, 0, 6 * MiB, default); // the first fragment is now in flight

        var probe = new Probe(engine);
        await Task.Delay(20);
        await engine.StageAsync(entry, truncate: true, default); // overwritten: the streaming upload stops
        var longest = await probe.StopAsync();
        Report("longest create while a streaming upload with a fragment in flight (500 ms) stopped", longest);

        Assert.True(longest < Brief, $"a create waited {longest.TotalMilliseconds:0} ms");
    }

    [Fact]
    public async Task A_new_file_renamed_while_it_uploads_moves_at_once()
    {
        var engine = await ReadyAsync(background: true);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = async () => { started.TrySetResult(); await Slow(1000); };
        var entry = await engine.CreateAsync(Root + "report.pdf.crdownload", false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, "pdf");
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + "report.pdf.crdownload", default);
        await started.Task;

        var watch = Stopwatch.StartNew();
        await engine.RenameAsync(Root + "report.pdf.crdownload", Root + "report.pdf", false, default);
        Report("rename of a new file while it uploads (1 s)", watch.Elapsed);
        var renamed = watch.Elapsed;
        Assert.NotNull(await engine.GetEntryAsync(Root + "report.pdf", default));
        Assert.Null(await engine.GetEntryAsync(Root + "report.pdf.crdownload", default));
        await engine.WaitForUploadsAsync();

        Assert.True(renamed < TimeSpan.FromMilliseconds(500), $"the rename took {renamed.TotalMilliseconds:0} ms");
        Assert.Equal(["report.pdf"], api.Paths);
        Assert.Equal("pdf", api.ContentOf("report.pdf"));
        Assert.Single(api.Uploads);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
        catch (IOException)
        {
        }
    }
}
