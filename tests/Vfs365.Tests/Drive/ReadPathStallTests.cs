using System.Runtime.CompilerServices;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>A silently dead connection never hangs a folder listing, a change feed read or a download; each fails and is tried again.</summary>
public sealed class ReadPathStallTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi fake = new();
    readonly StallingDriveApi api;
    readonly List<string> log = [];
    DateTimeOffset now = new(2026, 10, 24, 9, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    public ReadPathStallTests() => api = new StallingDriveApi(fake);

    DriveEngine Create() =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api, new ContentCacheOptions { NoProgressTimeout = Short }),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                CallTimeout = Short,
                LookupDeadline = Short,
                Random = () => 0.5,
                Log = line => { lock (log) { log.Add(line); } },
            });

    bool Logged(string text)
    {
        lock (log)
        {
            return log.Any(line => line.Contains(text));
        }
    }

    static byte[] Data(int length)
    {
        var bytes = new byte[length];
        new Random(365).NextBytes(bytes);
        return bytes;
    }

    static async Task<T> Within<T>(Task<T> task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(Patience)));
        return await task;
    }

    static async Task Within(Task task)
    {
        Assert.Same(task, await Task.WhenAny(task, Task.Delay(Patience)));
        await task;
    }

    [Fact]
    public async Task Offline_after_a_restart_restored_folders_and_cached_files_still_open()
    {
        fake.Add("docs", folder: true);
        fake.Add("docs/a.txt", content: "hello");
        var first = Create();
        await first.PollAsync("b!me");
        await first.ListAsync((await first.GetEntryAsync("\\", default))!, default);
        await first.ListAsync((await first.GetEntryAsync("\\docs", default))!, default);
        using (var source = await first.OpenContentAsync((await first.GetEntryAsync("\\docs\\a.txt", default))!, default))
        {
            var buffer = new byte[5];
            Assert.Equal(5, await source.ReadAsync(0, buffer, default));
        }
        var snapshots = first.ExportSnapshots(100);

        api.Offline = true;
        var second = Create();
        second.ImportSnapshots(snapshots);
        var file = await Within(second.GetEntryAsync("\\docs\\a.txt", default));
        Assert.Equal(5, file!.Size);
        Assert.Equal(["a.txt"], (await Within(second.ListAsync((await second.GetEntryAsync("\\docs", default))!, default))).Select(e => e.Name));
        using var cached = await Within(second.OpenContentAsync(file, default));
        var read = new byte[5];
        Assert.Equal(5, await cached.ReadAsync(0, read, default));
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString(read));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_folder_listing_that_stops_answering_fails_instead_of_hanging(bool ignoresToken)
    {
        fake.PageSize = 2;
        fake.Add("docs", folder: true);
        for (var i = 1; i <= 5; i++)
        {
            fake.Add($"docs/f{i}.txt");
        }
        var engine = Create();
        var folder = (await engine.GetEntryAsync("\\docs", default))!;
        (api.HangAfterPages, api.IgnoreTokens) = (1, ignoresToken);

        var listing = engine.ListAsync(folder, default);
        Assert.Same(listing, await Task.WhenAny(listing, Task.Delay(Patience)));
        await Assert.ThrowsAsync<TimeoutException>(() => listing);

        api.HangAfterPages = null;
        Assert.Equal(5, (await Within(engine.ListAsync(folder, default))).Count); // read again, not the broken load
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_change_feed_read_that_never_answers_counts_as_a_failure(bool ignoresToken)
    {
        fake.Add("docs", folder: true);
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\docs", default))!, default);
        (api.HangDelta, api.IgnoreTokens) = (true, ignoresToken);

        await Within(engine.PollAsync("b!me"));

        Assert.True(Logged("No answer from Microsoft 365"));
        var calls = api.DeltaCalls;
        await engine.PollAsync("b!me");
        Assert.Equal(calls, api.DeltaCalls); // backs off
        Assert.Empty(engine.PushDrives()); // a broken feed gets no push channel
        Assert.Equal(["b!me"], engine.ActiveDrives());
    }

    [Fact]
    public async Task A_change_feed_read_cancelled_by_the_client_timeout_backs_off()
    {
        var engine = Create();
        await engine.PollAsync("b!me");
        // What HttpClient throws when its own timeout fires: no token was cancelled
        api.FailDelta = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");

        await Within(engine.PollAsync("b!me"));

        Assert.True(Logged("HttpClient.Timeout"));
        var calls = api.DeltaCalls;
        await engine.PollAsync("b!me");
        Assert.Equal(calls, api.DeltaCalls);
    }

    [Theory]
    [InlineData(100_000, false)]
    [InlineData(100_000, true)]
    [InlineData(0, true)]
    public async Task A_download_that_stops_sending_fails_the_read_and_the_next_open_reads(long stallAfter, bool ignoresToken)
    {
        var data = Data(3 * 1024 * 1024);
        fake.Add("big.bin", data);
        var engine = Create();
        var file = (await engine.GetEntryAsync("\\big.bin", default))!;
        (api.StallAfterBytes, api.IgnoreTokens) = (stallAfter, ignoresToken);

        using (var source = await engine.OpenContentAsync(file, default))
        {
            var read = source.ReadAsync(200_000, new byte[4096], default).AsTask(); // past what was sent: chunks are served as they arrive
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(Patience)));
            var failure = await Assert.ThrowsAnyAsync<Exception>(() => read);
            Assert.Contains("No download progress", failure.Message);
        }

        api.StallAfterBytes = null;
        using var again = await engine.OpenContentAsync(file, default);
        var buffer = new byte[4096];
        Assert.Equal(4096, await Within(again.ReadAsync(2 * 1024 * 1024, buffer, default).AsTask()));
        Assert.Equal(data.AsSpan(2 * 1024 * 1024, 4096).ToArray(), buffer);
    }

    [Fact]
    public async Task A_slow_download_that_keeps_sending_is_not_given_up()
    {
        // 64 KiB every 50 ms: a 1 MiB block takes 0.8 s, more than twice the no-progress timeout
        var data = Data(1536 * 1024);
        fake.Add("slow.bin", data);
        var engine = Create();
        var file = (await engine.GetEntryAsync("\\slow.bin", default))!;
        (api.SlowPiece, api.PieceDelay) = (64 * 1024, TimeSpan.FromMilliseconds(50));

        using var source = await engine.OpenContentAsync(file, default);
        var buffer = new byte[4096];

        Assert.Equal(4096, await Within(source.ReadAsync(0, buffer, default).AsTask()));
        Assert.Equal(data.AsSpan(0, 4096).ToArray(), buffer);
    }

    [Fact]
    public async Task A_folder_loading_in_the_background_is_promoted_when_someone_waits_for_it()
    {
        fake.Add("scans", folder: true);
        for (var i = 0; i < 6; i++)
        {
            fake.Add($"scans/{i:D3}.pdf");
        }
        var engine = Create();
        var hold = new TaskCompletionSource();
        api.BeforeListing = () => hold.Task;
        for (var i = 0; i < 5; i++)
        {
            await engine.GetEntryAsync($"\\scans\\{i:D3}.pdf", default); // the fifth lists the folder, in the background
        }
        await Eventually(() => api.ListPriorities.Count == 1);
        var priority = api.ListPriorities.Single();
        Assert.NotNull(priority);
        Assert.True(priority.IsBackground);

        var listing = engine.ListAsync((await engine.GetEntryAsync("\\scans", default))!, default);
        await Eventually(() => !priority.IsBackground);
        hold.SetResult();

        Assert.Equal(6, (await Within(listing)).Count);
        Assert.Single(api.ListPriorities);
    }

    /// <summary>The docs folder with 20 files, listed while the feed fails (so its listing goes stale after ListingTtl).</summary>
    async Task<DriveEngine> StaleFolderAsync()
    {
        fake.DeltaUnavailable = true;
        fake.Add("docs", folder: true);
        for (var i = 0; i < 20; i++)
        {
            fake.Add($"docs/f{i:D2}.txt");
        }
        var engine = Create();
        await engine.ListAsync((await engine.GetEntryAsync("\\docs", default))!, default);
        now += TimeSpan.FromMinutes(2);
        return engine;
    }

    [Fact]
    public async Task Offline_only_the_first_lookup_waits_for_the_network_to_fail()
    {
        var engine = await StaleFolderAsync();
        (api.Offline, api.FailAfter) = (true, TimeSpan.FromMilliseconds(200));
        var calls = api.ItemCalls;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            Assert.NotNull(await Within(engine.GetEntryAsync($"\\docs\\f{i:D2}.txt", default)));
        }

        // Before: each of the 20 waited for its own failure (4 s here, 40 to 200 s on a black-holed network). Now the first does,
        // and the background refresh of the folder looks its ID up once
        Assert.InRange(api.ItemCalls - calls, 1, 2);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"{watch.ElapsedMilliseconds} ms");

        // 30 s later a lookup still answers at once, and one asks again in the background
        now += TimeSpan.FromSeconds(31);
        api.Offline = false;
        calls = api.ItemCalls;
        watch.Restart();
        Assert.NotNull(await Within(engine.GetEntryAsync("\\docs\\f00.txt", default)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"{watch.ElapsedMilliseconds} ms");
        await Eventually(() => api.ItemCalls > calls);

        // That worked, so the next lookup asks Microsoft 365 again (and waits for its failure, offline again)
        await Task.Delay(100);
        now += TimeSpan.FromSeconds(61);
        api.Offline = true;
        watch.Restart();
        Assert.NotNull(await Within(engine.GetEntryAsync("\\docs\\f01.txt", default)));
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(150), $"{watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task A_lookup_that_gets_no_answer_falls_back_to_the_stale_listing_after_its_deadline()
    {
        var engine = await StaleFolderAsync();
        api.HangItems = true;
        fake.PageSize = 2;
        api.HangAfterPages = 0; // the refresh of the folder hangs too

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.NotNull(await Within(engine.GetEntryAsync("\\docs\\f00.txt", default)));
        Assert.NotNull(await Within(engine.GetEntryAsync("\\docs\\f01.txt", default)));

        Assert.InRange(watch.Elapsed, Short, Short * 6); // the first waits out its deadline, the second answers at once
        Assert.Null(await Within(engine.GetEntryAsync("\\docs\\nope.txt", default)));
    }

    [Fact]
    public async Task While_the_client_holds_background_requests_no_change_feed_is_read()
    {
        fake.Add("docs", folder: true);
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\docs", default))!, default);
        var calls = api.DeltaCalls;

        engine.HotPollingPaused = true;
        now += TimeSpan.FromMinutes(5);
        await engine.PollAsync("b!me");
        await engine.NotifiedAsync("b!me");
        await engine.PollActiveAsync();
        Assert.Equal(calls, api.DeltaCalls);
        Assert.NotNull(await Within(engine.GetEntryAsync("\\docs\\x.txt", default).ContinueWith(_ => (object)"done"))); // no catch-up to wait for

        engine.HotPollingPaused = false;
        await engine.PollActiveAsync();
        Assert.Equal(calls + 1, api.DeltaCalls);
    }

    [Fact]
    public async Task A_change_feed_read_held_by_the_client_during_throttling_breaks_nothing()
    {
        fake.Add("docs", folder: true);
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\docs", default))!, default);
        api.HangDelta = true;

        // A read starts, then the client starts holding background requests (a 429 elsewhere); the read times out meanwhile
        var read = engine.PollAsync("b!me");
        engine.HotPollingPaused = true;
        await Within(read);
        engine.HotPollingPaused = false;

        Assert.Equal(["b!me"], engine.PushDrives()); // not broken: push stays, listings stay current
        Assert.False(Logged("trying again in"));
        api.HangDelta = false;
        var calls = api.DeltaCalls;
        await engine.PollAsync("b!me"); // no back-off
        Assert.Equal(calls + 1, api.DeltaCalls);
    }

    static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// FakeDriveApi with what the read path meets on a real network: calls that never answer (a dead connection, also ignoring their
/// token), slow downloads, Graph's 404 for a folder that isn't there and Graph's eTag form ("{id},version").
/// </summary>
sealed class StallingDriveApi(FakeDriveApi inner) : IDriveApi
{
    /// <summary>Change feed reads (and baselines) never answer.</summary>
    public bool HangDelta { get; set; }

    /// <summary>Change feed reads throw this.</summary>
    public Exception? FailDelta { get; set; }

    /// <summary>Held after a change feed page was read, before it is returned: a read that started before what happens meanwhile.</summary>
    public Func<Task>? AfterDeltaRead { get; set; }

    /// <summary>Listings answer this many pages, then never again.</summary>
    public int? HangAfterPages { get; set; }

    /// <summary>Held before each listing request.</summary>
    public Func<Task>? BeforeListing { get; set; }

    /// <summary>The priority each listing request was made at, and the folders listed.</summary>
    public List<PriorityHolder?> ListPriorities { get; } = [];
    public List<string> Listed { get; } = [];

    /// <summary>Folders the user may not list (access to a folder below only, as with broken inheritance): 403.</summary>
    public HashSet<string> Unlistable { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Downloads stop sending after this many bytes, without ending.</summary>
    public long? StallAfterBytes { get; set; }

    /// <summary>Downloads send this many bytes at a time, PieceDelay apart.</summary>
    public int? SlowPiece { get; set; }
    public TimeSpan PieceDelay { get; set; }

    /// <summary>Calls that hang don't end when their token is cancelled either.</summary>
    public bool IgnoreTokens { get; set; }

    /// <summary>ETags in Graph's form, "{id},version", instead of the fake's "eN".</summary>
    public bool GraphTags { get; set; }

    /// <summary>No network: every read fails at once, as with no connection.</summary>
    public bool Offline { get; set; }

    /// <summary>With Offline, each read fails only after this long (a black-holed network: a TCP connect timeout).</summary>
    public TimeSpan? FailAfter { get; set; }

    /// <summary>Item lookups never answer.</summary>
    public bool HangItems { get; set; }

    /// <summary>Change feed pages served before the fake's own (for changes the fake can't make, such as children reported first).</summary>
    public Queue<DeltaPage> Scripted { get; } = new();

    public int DeltaCalls, ItemCalls, ListingCalls;

    internal Task Hang(CancellationToken ct) => IgnoreTokens ? new TaskCompletionSource().Task : Task.Delay(Timeout.Infinite, ct);

    async Task OnlineAsync(CancellationToken ct)
    {
        if (Offline)
        {
            if (FailAfter is { } wait)
            {
                await Task.Delay(wait, ct);
            }
            throw new HttpRequestException("No such host is known. (graph.microsoft.com:443)");
        }
    }

    public async IAsyncEnumerable<IReadOnlyList<DriveItemInfo>> ListChildrenAsync(string driveId, string folderPath, [EnumeratorCancellation] CancellationToken ct)
    {
        Interlocked.Increment(ref ListingCalls);
        await OnlineAsync(ct);
        lock (ListPriorities)
        {
            ListPriorities.Add(RequestPriority.Current);
            Listed.Add(folderPath);
        }
        if (BeforeListing is { } hold)
        {
            await hold();
        }
        if (folderPath.Length > 0 && inner.ItemAt(folderPath) is not { IsFolder: true })
        {
            throw new RemoteException(RemoteError.NotFound, "404 itemNotFound");
        }
        if (Unlistable.Contains(folderPath))
        {
            throw new RemoteException(RemoteError.AccessDenied, "403 accessDenied");
        }
        var pages = 0;
        await foreach (var page in inner.ListChildrenAsync(driveId, folderPath, ct))
        {
            if (HangAfterPages is { } limit && pages++ >= limit)
            {
                await Hang(ct);
            }
            yield return page.Select(Map).ToList();
        }
    }

    public async Task<string> GetLatestDeltaLinkAsync(string driveId, CancellationToken ct)
    {
        await OnlineAsync(ct);
        Interlocked.Increment(ref DeltaCalls);
        if (HangDelta)
        {
            await Hang(ct);
        }
        return await inner.GetLatestDeltaLinkAsync(driveId, ct);
    }

    public async Task<DeltaPage> GetDeltaAsync(string link, CancellationToken ct)
    {
        await OnlineAsync(ct);
        Interlocked.Increment(ref DeltaCalls);
        if (FailDelta is { } failure)
        {
            throw failure;
        }
        if (HangDelta)
        {
            await Hang(ct);
        }
        var page = Scripted.TryDequeue(out var scripted) ? scripted : await inner.GetDeltaAsync(link, ct);
        if (AfterDeltaRead is { } hold)
        {
            await hold();
        }
        return page with { Changes = page.Changes.Select(c => c with { Item = c.Item is null ? null : Map(c.Item) }).ToList() };
    }

    public async Task<DriveItemInfo?> GetItemAsync(string driveId, string path, CancellationToken ct)
    {
        Interlocked.Increment(ref ItemCalls);
        await OnlineAsync(ct);
        if (HangItems)
        {
            await Hang(ct);
        }
        return await inner.GetItemAsync(driveId, path, ct) is { } item ? Map(item) : null;
    }

    public async Task<DriveItemInfo?> GetItemByIdAsync(string driveId, string itemId, CancellationToken ct)
    {
        Interlocked.Increment(ref ItemCalls);
        await OnlineAsync(ct);
        return await inner.GetItemByIdAsync(driveId, itemId, ct) is { } item ? Map(item) : null;
    }

    public async Task<Stream> OpenReadAsync(string driveId, string itemId, string? contentTag, long offset, long length, CancellationToken ct)
    {
        await OnlineAsync(ct);
        var stream = await inner.OpenReadAsync(driveId, itemId, Unmap(contentTag), offset, length, ct);
        return StallAfterBytes is null && SlowPiece is null ? stream : new StallingStream(stream, this);
    }

    public async Task<DriveItemInfo> UploadAsync(string driveId, UploadTarget target, string sourceFile, CancellationToken ct) =>
        Map(await inner.UploadAsync(driveId, Unmap(target), sourceFile, ct));

    public Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct) => inner.CreateUploadSessionAsync(driveId, Unmap(target), ct);

    public async Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct) =>
        await inner.UploadFragmentAsync(session, sourceFile, offset, length, totalSize, ct) is { } item ? Map(item) : null;

    public Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct) => inner.CancelUploadSessionAsync(session, ct);

    public async Task<DriveItemInfo> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct) =>
        Map(await inner.CreateFolderAsync(driveId, parentPath, name, ct));

    public async Task<DriveItemInfo> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct) =>
        Map(await inner.MoveAsync(driveId, itemId, newParentId, newName, ct));

    public Task DeleteAsync(string driveId, string itemId, CancellationToken ct) => inner.DeleteAsync(driveId, itemId, ct);

    DriveItemInfo Map(DriveItemInfo item) =>
        GraphTags && item.ETag is { Length: > 1 } tag && tag[0] == 'e' && long.TryParse(tag.AsSpan(1), out var version)
            ? item with { ETag = $"\"{{{item.Id}}},{version}\"" }
            : item;

    string? Unmap(string? tag) =>
        GraphTags && tag is not null && tag.LastIndexOf(',') is var split and > 0 && long.TryParse(tag.AsSpan(split + 1).Trim('"'), out var version)
            ? $"e{version}"
            : tag;

    UploadTarget Unmap(UploadTarget target) => target with { IfMatch = Unmap(target.IfMatch) };

    sealed class StallingStream(Stream content, StallingDriveApi api) : Stream
    {
        long sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (api.StallAfterBytes is { } stop)
            {
                if (sent >= stop)
                {
                    await api.Hang(ct);
                }
                buffer = buffer[..(int)Math.Min(buffer.Length, stop - sent)];
            }
            if (api.SlowPiece is { } piece)
            {
                await Task.Delay(api.PieceDelay, ct);
                buffer = buffer[..Math.Min(buffer.Length, piece)];
            }
            var read = await content.ReadAsync(buffer, ct);
            sent += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                content.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
