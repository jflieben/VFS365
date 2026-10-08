using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

public sealed class WalkTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly List<string> log = [];
    DateTimeOffset now = new(2026, 10, 7, 9, 0, 0, TimeSpan.Zero);

    DriveEngine Create(FakeDriveApi api, int walkThreshold = 3, int prefetchAhead = 10) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                WalkThreshold = walkThreshold,
                PrefetchConcurrency = 2,
                PrefetchAhead = prefetchAhead,
                Log = line => { lock (log) { log.Add(line); } },
            });

    static FakeDriveApi Tree(int folders)
    {
        var api = new FakeDriveApi();
        for (var f = 0; f < folders; f++)
        {
            api.Add($"f{f:D2}", folder: true);
            api.Add($"f{f:D2}/sub", folder: true);
            api.Add($"f{f:D2}/a.txt");
            api.Add($"f{f:D2}/sub/b.txt");
        }
        return api;
    }

    static async Task<string[]> Names(DriveEngine engine, string path) =>
        (await engine.ListAsync((await engine.GetEntryAsync(path, default))!, default)).Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Waits until at least <paramref name="expected"/> listings were asked for (busy build machines), then until no more come for 20 ms.</summary>
    static async Task Settle(FakeDriveApi api, int expected = 0)
    {
        for (var waited = 0; api.ListCalls < expected && waited < 10_000; waited += 20)
        {
            await Task.Delay(20);
        }
        for (int last = -1, i = 0; i < 100 && last != api.ListCalls; i++)
        {
            last = api.ListCalls;
            await Task.Delay(20);
        }
    }

    /// <summary>Opens the root, f00 and f00\sub: three loads going two levels down.</summary>
    static async Task StartWalk(DriveEngine engine)
    {
        await engine.PollAsync("b!me");
        await Names(engine, "\\");
        await Names(engine, "\\f00");
        await Names(engine, "\\f00\\sub");
    }

    [Fact]
    public async Task A_tree_walk_gets_folders_loaded_ahead_and_each_folder_listed_once()
    {
        var api = Tree(10);
        var engine = Create(api);
        await StartWalk(engine);
        await Settle(api);
        Assert.InRange(api.ListCalls, 4, 3 + 10);

        for (var f = 1; f < 10; f++)
        {
            Assert.Equal(["a.txt", "sub"], await Names(engine, $"\\f{f:D2}"));
            Assert.Equal(["b.txt"], await Names(engine, $"\\f{f:D2}\\sub"));
        }
        await Settle(api, 21);

        Assert.Equal(21, api.ListCalls);
        Assert.Contains(log, line => line.Contains("tree walk on drive b!me"));
        AssertAllLoadedAheadWereUsed(await EndWalk(engine));
    }

    [Fact]
    public async Task A_breadth_first_walk_gets_its_next_level_loaded_ahead()
    {
        // .NET's recursive enumeration reads a whole level before going deeper
        var api = Tree(10);
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\");
        for (var f = 0; f < 10; f++)
        {
            await Names(engine, $"\\f{f:D2}");
        }
        await Names(engine, "\\f00\\sub");
        await Settle(api, 21);
        Assert.Equal(21, api.ListCalls);

        for (var f = 1; f < 10; f++)
        {
            Assert.Equal(["b.txt"], await Names(engine, $"\\f{f:D2}\\sub"));
        }
        Assert.Equal(21, api.ListCalls);
        Assert.Contains("9 folder(s) loaded ahead, 9 of them opened", await EndWalk(engine));
    }

    async Task<string> EndWalk(DriveEngine engine)
    {
        now += TimeSpan.FromSeconds(60);
        await engine.PollActiveAsync();
        lock (log)
        {
            return log.Last(line => line.Contains("ended:"));
        }
    }

    static void AssertAllLoadedAheadWereUsed(string ended)
    {
        var match = System.Text.RegularExpressions.Regex.Match(ended, @"ended: (\d+) folder\(s\) loaded ahead, (\d+) of them opened");
        Assert.True(match.Success, ended);
        Assert.True(int.Parse(match.Groups[1].Value) > 0, ended);
        Assert.Equal(match.Groups[1].Value, match.Groups[2].Value);
    }

    [Fact]
    public async Task A_walk_that_stops_leaves_at_most_the_lookahead_unused()
    {
        var api = Tree(50);
        var engine = Create(api, prefetchAhead: 5);
        await StartWalk(engine);
        await Settle(api, 3 + 5);
        Assert.Equal(3 + 5, api.ListCalls);

        now += TimeSpan.FromSeconds(60);
        await engine.PollActiveAsync();
        Assert.Contains(log, line => line.Contains("ended: 5 folder(s) loaded ahead, 0 of them opened"));

        await Names(engine, "\\f40");
        await Settle(api, 3 + 5 + 1);
        Assert.Equal(3 + 5 + 1, api.ListCalls);
    }

    [Fact]
    public async Task Opening_many_folders_side_by_side_is_no_walk()
    {
        // Explorer drawing folder thumbnails opens every subfolder of the folder on screen
        var api = Tree(10);
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\");
        for (var f = 0; f < 6; f++)
        {
            await Names(engine, $"\\f{f:D2}");
        }
        await Settle(api, 7);

        Assert.Equal(7, api.ListCalls);
    }

    [Fact]
    public async Task Browsing_a_few_folders_loads_nothing_ahead()
    {
        var api = Tree(10);
        var engine = Create(api, walkThreshold: 12);
        await StartWalk(engine);
        await Settle(api);

        Assert.Equal(3, api.ListCalls);
    }

    [Fact]
    public async Task No_lookahead_when_it_is_off()
    {
        var api = Tree(10);
        var engine = Create(api, prefetchAhead: 0);
        await StartWalk(engine);
        await Settle(api);

        Assert.Equal(3, api.ListCalls);
    }

    [Fact]
    public async Task A_drive_in_use_is_read_every_hot_interval_even_with_push()
    {
        var api = new FakeDriveApi();
        var engine = Create(api, walkThreshold: 0);
        engine.SetPush("b!me", true);
        await engine.PollAsync("b!me");
        await Names(engine, "\\");
        var calls = api.DeltaCalls;

        now += TimeSpan.FromSeconds(21);
        await engine.PollActiveAsync();
        Assert.Equal(calls + 1, api.DeltaCalls);

        now += TimeSpan.FromMinutes(3);
        await engine.PollActiveAsync();
        Assert.Equal(calls + 1, api.DeltaCalls);

        engine.HotPollingPaused = true;
        await Names(engine, "\\");
        now += TimeSpan.FromSeconds(21);
        await engine.PollActiveAsync();
        Assert.Equal(calls + 1, api.DeltaCalls);
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
