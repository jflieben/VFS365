using System.Collections;
using System.Reflection;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>
/// Folder listings beyond MaxCachedItems: the time a used listing is kept shrinks with the overshoot, so a walk through a big tree
/// stays near the cap, and what is loading, in use or holds local changes stays.
/// </summary>
public sealed class ListingEvictionTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly List<string> log = [];
    DateTimeOffset now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    DriveEngine Create(IDriveApi api, int maxCachedItems, int walkThreshold = 0) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                MaxCachedItems = maxCachedItems,
                WalkThreshold = walkThreshold,
                UploadNewFilesInBackground = true,
                Log = line => { lock (log) { log.Add(line); } },
            });

    static async Task List(DriveEngine engine, string path) => await engine.ListAsync((await engine.GetEntryAsync(path, default))!, default);

    static void AddFolder(FakeDriveApi api, string path, int files)
    {
        api.Add(path, folder: true);
        for (var i = 0; i < files; i++)
        {
            api.Add($"{path}/file-{i:D3}.txt");
        }
    }

    /// <summary>What the cached listings count as for MaxCachedItems: their items plus three per listing.</summary>
    static long Cached(DriveEngine engine)
    {
        var listings = (IDictionary)typeof(DriveEngine).GetField("listings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(engine)!;
        var size = 0L;
        foreach (var listing in listings.Values)
        {
            size += ((ICollection)listing!.GetType().GetProperty("Items")!.GetValue(listing)!).Count + 3;
        }
        return size;
    }

    static bool IsCached(DriveEngine engine, string drivePath) => ReadPathTesting.ListingAt(engine, drivePath) is not null;

    [Theory]
    [InlineData(false, 1.5)] // not noticed as a walk: the keep time shrinks with the overshoot alone
    [InlineData(true, 1.25)] // noticed: the folders the walk passed go first
    public async Task A_walk_past_the_cap_stays_near_the_cap(bool noticed, double within)
    {
        // 300 folders of 50 files, one every half second: 240 of them (12,720 items) are used within the two minutes a listing is kept
        const int cap = 5_000, folders = 300;
        var api = new FakeDriveApi();
        api.Add("W", folder: true);
        api.Add("W/A", folder: true);
        for (var f = 0; f < folders; f++)
        {
            AddFolder(api, $"W/A/d{f:D3}", 50);
        }
        var engine = Create(api, cap, walkThreshold: noticed ? 12 : 0);
        await engine.PollAsync("b!me");
        await List(engine, "\\W");
        await List(engine, "\\W\\A");

        var peak = 0L;
        for (var f = 0; f < folders; f++)
        {
            await List(engine, $"\\W\\A\\d{f:D3}");
            now += TimeSpan.FromSeconds(0.5);
            await engine.PollActiveAsync();
            peak = Math.Max(peak, Cached(engine));
        }

        Assert.InRange(peak, cap * 0.9, cap * within);
        Assert.InRange(Cached(engine), cap * 0.5, cap * within);
        Assert.True(IsCached(engine, "W/A")); // above where the walk is
        Assert.True(IsCached(engine, $"W/A/d{folders - 1:D3}"));
        Assert.False(IsCached(engine, "W/A/d000"));
        lock (log)
        {
            Assert.Equal(noticed, log.Any(line => line.Contains("tree walk")));
            Assert.Contains(log, line => line.Contains("left memory"));
        }

        await List(engine, "\\W\\A\\d000"); // what left memory is read again when opened
        Assert.True(IsCached(engine, "W/A/d000"));
    }

    [Fact]
    public async Task Listings_loading_in_use_or_with_local_changes_stay_when_older_ones_go()
    {
        var fake = new FakeDriveApi();
        for (var f = 0; f < 6; f++)
        {
            AddFolder(fake, $"old{f}", 100);
        }
        AddFolder(fake, "busy", 100);
        AddFolder(fake, "mid", 100);
        AddFolder(fake, "slow", 100);
        fake.Add("deep", folder: true);
        AddFolder(fake, "deep/child", 100);
        var api = new StallingDriveApi(fake);
        var engine = Create(api, maxCachedItems: 800);
        await engine.PollAsync("b!me");
        // A new file in busy that can't upload yet: local state in that folder, which then is the longest unused of all
        await List(engine, "\\busy");
        var upload = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.BeforeUpload = () => upload.Task;
        var created = await engine.CreateAsync("\\busy\\new.txt", false, default);
        await File.WriteAllTextAsync(created.StagingPath!, "not uploaded yet");
        engine.MarkDirty(created);
        await engine.CommitAsync("\\busy\\new.txt", default);
        Assert.Single(engine.Unsaved());
        now += TimeSpan.FromSeconds(1);
        for (var f = 0; f < 6; f++)
        {
            await List(engine, $"\\old{f}");
            now += TimeSpan.FromSeconds(1);
        }
        await List(engine, "\\deep");

        now += TimeSpan.FromMinutes(10);
        await List(engine, "\\mid");
        now += TimeSpan.FromSeconds(50);
        await List(engine, "\\deep\\child"); // deep counts as used with it
        now += TimeSpan.FromSeconds(10);
        var listing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeListing = () => listing.Task;
        var slow = engine.ListAsync((await engine.GetEntryAsync("\\slow", default))!, default);
        Assert.True(Cached(engine) is > 800 and < 1200); // over the cap, by less than half: listings used in the last minute stay

        await engine.PollActiveAsync();

        Assert.InRange(Cached(engine), 400, 800 * 9 / 10);
        Assert.False(IsCached(engine, "old0"));
        Assert.True(IsCached(engine, "busy")); // not used for 11 minutes, but it holds a file waiting to upload
        Assert.True(IsCached(engine, "mid")); // used a minute ago
        Assert.True(IsCached(engine, "deep/child"));
        Assert.True(IsCached(engine, "deep"));
        Assert.True(IsCached(engine, "slow")); // loading
        lock (log)
        {
            Assert.Contains(log, line => line.Contains("left memory"));
        }

        listing.SetResult();
        Assert.Equal(100, (await slow).Count);
        upload.SetResult();
        await engine.WaitForUploadsAsync();
        Assert.Empty(engine.Unsaved());
        var lists = fake.ListCalls;
        Assert.Equal(101, (await engine.ListAsync((await engine.GetEntryAsync("\\busy", default))!, default)).Count); // from its kept listing
        Assert.Equal(lists, fake.ListCalls);
    }

    [Theory]
    [InlineData(2, true)] // a little over the cap: a listing used a minute ago stays
    [InlineData(9, false)] // far over it: only the last 20 seconds stay
    public async Task The_time_a_used_listing_is_kept_shrinks_with_the_overshoot(int fresh, bool kept)
    {
        var api = new FakeDriveApi();
        AddFolder(api, "long-ago", 100);
        AddFolder(api, "minute-ago", 100);
        AddFolder(api, "just-now", 100);
        for (var f = 0; f < fresh; f++)
        {
            AddFolder(api, $"fresh{f}", 100);
        }
        var engine = Create(api, maxCachedItems: 500);
        await engine.PollAsync("b!me");
        await List(engine, "\\long-ago");
        now += TimeSpan.FromMinutes(10);
        await List(engine, "\\minute-ago");
        now += TimeSpan.FromSeconds(55);
        await List(engine, "\\just-now");
        now += TimeSpan.FromSeconds(5);
        for (var f = 0; f < fresh; f++)
        {
            await List(engine, $"\\fresh{f}");
        }

        await engine.PollActiveAsync();

        Assert.False(IsCached(engine, "long-ago"));
        Assert.Equal(kept, IsCached(engine, "minute-ago"));
        Assert.True(IsCached(engine, "just-now"));
        Assert.All(Enumerable.Range(0, fresh), f => Assert.True(IsCached(engine, $"fresh{f}")));
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
