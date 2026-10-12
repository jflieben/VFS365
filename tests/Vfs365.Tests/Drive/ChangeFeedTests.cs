using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

public sealed class ChangeFeedTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly List<EngineChange> changes = [];
    DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    readonly List<string> log = [];

    DriveEngine Create(FakeDriveApi api, int maxCachedItems = 250_000) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                MaxCachedItems = maxCachedItems,
                Changed = change => { lock (changes) { changes.Add(change); } },
                Log = line => { lock (log) { log.Add(line); } },
            });

    static async Task<string[]> Names(DriveEngine engine, string path) =>
        (await engine.ListAsync((await engine.GetEntryAsync(path, default))!, default)).Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    bool Saw(string path, ChangeKind kind)
    {
        lock (changes)
        {
            return changes.Any(c => c.Path == path && c.Kind == kind);
        }
    }

    static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task Remote_changes_reach_cached_listings_without_listing_again()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/a.txt");
        api.Add("docs/b.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["a.txt", "b.txt"], await Names(engine, "\\docs"));
        var lists = api.ListCalls;

        api.Add("docs/c.txt");
        api.RemoteEdit("docs/a.txt", "changed");
        api.RemoteDelete("docs/b.txt");
        await engine.PollAsync("b!me");

        Assert.Equal(["a.txt", "c.txt"], await Names(engine, "\\docs"));
        Assert.Equal(lists, api.ListCalls);
        Assert.Equal(7, (await engine.GetEntryAsync("\\docs\\a.txt", default))!.Size);
        Assert.True(Saw("\\docs\\c.txt", ChangeKind.Added));
        Assert.True(Saw("\\docs\\a.txt", ChangeKind.Modified));
        Assert.True(Saw("\\docs\\b.txt", ChangeKind.Removed));
    }

    [Fact]
    public async Task Own_uploads_coming_back_through_the_feed_are_not_reported()
    {
        var api = new FakeDriveApi();
        api.Add("a.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\");

        await engine.CreateAsync("\\new.txt", false, default);
        await engine.CommitAsync("\\new.txt", default);
        await engine.PollAsync("b!me");

        Assert.Equal(["a.txt", "new.txt"], await Names(engine, "\\"));
        Assert.False(Saw("\\new.txt", ChangeKind.Added));
    }

    [Fact]
    public async Task Remote_folder_rename_drops_the_cached_subtree()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/sub", folder: true);
        api.Add("docs/sub/x.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["sub"], await Names(engine, "\\docs"));
        Assert.Equal(["x.txt"], await Names(engine, "\\docs\\sub"));

        api.RemoteMove("docs/sub", "docs/renamed");
        await engine.PollAsync("b!me");

        Assert.Equal(["renamed"], await Names(engine, "\\docs"));
        Assert.Null(await engine.GetEntryAsync("\\docs\\sub\\x.txt", default));
        Assert.Equal(["x.txt"], await Names(engine, "\\docs\\renamed"));
        Assert.True(Saw("\\docs\\sub", ChangeKind.Removed));
        Assert.True(Saw("\\docs\\renamed", ChangeKind.Added));
    }

    [Fact]
    public async Task Expired_feed_reads_folders_again()
    {
        var api = new FakeDriveApi();
        api.Add("a.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["a.txt"], await Names(engine, "\\"));
        var lists = api.ListCalls;

        api.ExpireDeltaLinks = true;
        await engine.PollAsync("b!me");
        api.ExpireDeltaLinks = false;
        api.Add("b.txt");

        Assert.Equal(["a.txt", "b.txt"], await Names(engine, "\\"));
        Assert.Equal(lists + 1, api.ListCalls);
        Assert.True(Saw("\\", ChangeKind.Modified));
    }

    [Fact]
    public async Task After_a_refresh_every_folder_is_read_again_when_opened()
    {
        // A change the feed doesn't see (as if it was missed): only a refresh brings it in
        var api = new FakeDriveApi();
        api.Add("a.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["a.txt"], await Names(engine, "\\"));
        api.AddUnnoticed("b.txt");
        Assert.Equal(["a.txt"], await Names(engine, "\\"));
        var lists = api.ListCalls;

        engine.ExpireListings();
        Assert.Equal(lists, api.ListCalls); // nothing is read until a folder is opened
        var stale = await Names(engine, "\\");
        await Eventually(() => Saw("\\b.txt", ChangeKind.Added));

        Assert.Equal(["a.txt"], stale);
        Assert.Equal(lists + 1, api.ListCalls);
        Assert.Equal(["a.txt", "b.txt"], await Names(engine, "\\"));
    }

    [Fact]
    public async Task Stale_listing_answers_at_once_and_reports_what_the_refresh_found()
    {
        var api = new FakeDriveApi { DeltaUnavailable = true };
        api.Add("a.txt");
        var engine = Create(api);
        Assert.Equal(["a.txt"], await Names(engine, "\\"));

        api.Add("b.txt");
        now += TimeSpan.FromMinutes(2);
        var stale = await Names(engine, "\\");
        await Eventually(() => Saw("\\b.txt", ChangeKind.Added));

        Assert.Equal(["a.txt"], stale);
        Assert.Equal(["a.txt", "b.txt"], await Names(engine, "\\"));
    }

    [Fact]
    public async Task Large_folder_shows_its_first_page_before_the_rest_arrives()
    {
        var api = new FakeDriveApi { PageSize = 2 };
        for (var i = 1; i <= 5; i++)
        {
            api.Add($"f{i}.txt");
        }
        var rest = new TaskCompletionSource();
        api.BeforeNextPage = () => rest.Task;
        var engine = Create(api);
        var root = (await engine.GetEntryAsync("\\", default))!;

        await using var batches = engine.ListStreamAsync(root, default).GetAsyncEnumerator();
        Assert.True(await batches.MoveNextAsync());
        var first = batches.Current.Select(e => e.Name).ToList();
        rest.SetResult();
        var all = new List<string>(first);
        while (await batches.MoveNextAsync())
        {
            all.AddRange(batches.Current.Select(e => e.Name));
        }

        Assert.Equal(["f1.txt", "f2.txt"], first);
        Assert.Equal(5, all.Distinct().Count());
        Assert.Equal(3, api.ListPages);
    }

    [Fact]
    public async Task Snapshot_restores_listings_and_catches_up_on_first_use()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/a.txt");
        var first = Create(api);
        await first.PollAsync("b!me");
        await Names(first, "\\");
        await Names(first, "\\docs");
        var snapshots = first.ExportSnapshots(100);

        api.Add("docs/b.txt");
        var second = Create(api);
        second.ImportSnapshots(snapshots);
        var lists = api.ListCalls;
        await Names(second, "\\docs");
        await second.PollAsync("b!me");

        Assert.Equal(["a.txt", "b.txt"], await Names(second, "\\docs"));
        Assert.Equal(lists, api.ListCalls);
        Assert.Equal(2, snapshots.Single().Folders.Count);
    }

    [Fact]
    public async Task New_libraries_appear_and_known_ones_keep_their_drive()
    {
        var resolves = 0;
        LibraryEntry Library(string site, string title) => new($"k-{site}-{title}", "s", "w", "l", $"https://x.sharepoint.com/sites/{site}", site, title, false, 1);
        var api = new FakeDriveApi();
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [Library("Finance", "Documents")],
            (_, _) => { Interlocked.Increment(ref resolves); return Task.FromResult("b!finance"); });
        var engine = new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), Changed = change => { lock (changes) { changes.Add(change); } } });
        await engine.GetEntryAsync("\\Sites\\Finance\\Documents\\x.txt", default);

        engine.SetLibraries([Library("Finance", "Documents"), Library("HR", "Policies")]);
        await engine.GetEntryAsync("\\Sites\\Finance\\Documents\\x.txt", default);

        Assert.Equal(["Finance", "HR"], await Names(engine, "\\Sites"));
        Assert.Equal(1, resolves);
        Assert.True(Saw("\\Sites", ChangeKind.Modified));
        Assert.Equal("\\Sites\\Finance\\Documents", ns.VolumePathOf("b!finance"));
    }

    [Fact]
    public async Task A_remote_folder_rename_is_seen_when_its_parent_is_not_cached()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/sub", folder: true);
        api.Add("docs/sub/x.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["x.txt"], await Names(engine, "\\docs\\sub")); // \docs itself is never listed

        // A colleague renames the folder and makes a new one with the old name
        api.RemoteMove("docs/sub", "docs/renamed");
        api.Add("docs/sub", folder: true);
        await engine.PollAsync("b!me");

        Assert.Null(await engine.GetEntryAsync("\\docs\\sub\\x.txt", default));
        Assert.Empty(await Names(engine, "\\docs\\sub"));
        Assert.True(Saw("\\docs\\sub", ChangeKind.Removed));
    }

    [Fact]
    public async Task A_folder_moved_elsewhere_under_its_own_name_is_seen()
    {
        var api = new FakeDriveApi();
        api.Add("a", folder: true);
        api.Add("b", folder: true);
        api.Add("a/sub", folder: true);
        api.Add("a/sub/x.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["x.txt"], await Names(engine, "\\a\\sub"));

        api.RemoteMove("a/sub", "b/sub");
        await engine.PollAsync("b!me");

        Assert.Null(await engine.GetEntryAsync("\\a\\sub\\x.txt", default));
        Assert.Equal(["x.txt"], await Names(engine, "\\b\\sub"));
    }

    [Fact]
    public async Task A_change_while_a_folder_loads_is_not_lost()
    {
        var api = new FakeDriveApi { PageSize = 2 };
        api.Add("docs", folder: true);
        for (var i = 1; i <= 5; i++)
        {
            api.Add($"docs/f{i}.txt");
        }
        var engine = Create(api);
        await engine.PollAsync("b!me");
        var rest = new TaskCompletionSource();
        api.BeforeNextPage = () => rest.Task;
        var loading = engine.ListAsync((await engine.GetEntryAsync("\\docs", default))!, default);

        api.Add("docs/new.txt"); // after the listing was read, before it finished
        await engine.PollAsync("b!me");
        rest.SetResult();
        await loading;
        var lists = api.ListCalls;

        Assert.Contains("new.txt", await Names(engine, "\\docs"));
        Assert.Equal(lists, api.ListCalls);
    }

    [Fact]
    public async Task Restored_listings_answer_for_a_day_and_are_then_read_again_without_lookups()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/a.txt");
        var first = Create(api);
        await first.PollAsync("b!me");
        await Names(first, "\\");
        await Names(first, "\\docs");
        var snapshots = first.ExportSnapshots(100);

        now += TimeSpan.FromHours(5); // the next morning
        var second = Create(api);
        second.ImportSnapshots(snapshots);
        var (lists, gets) = (api.ListCalls, api.GetCalls);
        Assert.Equal(["a.txt"], await Names(second, "\\docs"));
        Assert.NotNull(await second.GetEntryAsync("\\docs\\a.txt", default));
        await second.PollAsync("b!me");
        Assert.Equal((lists, gets), (api.ListCalls, api.GetCalls));

        now += TimeSpan.FromHours(20); // older than a day: read again in the background when opened
        Assert.Equal(["a.txt"], await Names(second, "\\docs"));
        await Eventually(() => api.ListCalls == lists + 1);
        Assert.Equal(gets, api.GetCalls); // the folder's ID was known
    }

    [Fact]
    public async Task A_long_catch_up_on_a_drive_with_few_cached_folders_starts_over()
    {
        var api = new FakeDriveApi { DeltaPageSize = 1 };
        api.Add("docs", folder: true);
        api.Add("docs/a.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["a.txt"], await Names(engine, "\\docs"));
        for (var i = 0; i < 30; i++)
        {
            api.Add($"other{i}.txt");
        }
        var (lists, deltas) = (api.ListCalls, api.DeltaCalls);

        await engine.PollAsync("b!me");

        Assert.Equal(deltas + 10 + 1, api.DeltaCalls); // 10 pages, then a new start
        Assert.Contains(log, line => line.Contains("starting over"));
        Assert.Equal(["a.txt"], await Names(engine, "\\docs"));
        Assert.Equal(lists + 1, api.ListCalls);
    }

    [Fact]
    public async Task A_library_glanced_at_once_gets_no_change_feed_until_it_is_used()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/a.txt");
        var engine = Create(api);

        Assert.Equal(["docs"], await Names(engine, "\\"));
        Assert.Empty(engine.ActiveDrives());
        Assert.Equal(0, api.DeltaCalls);

        Assert.Equal(["a.txt"], await Names(engine, "\\docs"));
        Assert.Equal(["b!me"], engine.ActiveDrives());
        await Eventually(() => api.DeltaCalls > 0);
    }

    [Fact]
    public async Task Least_recently_used_listings_leave_memory_beyond_the_budget()
    {
        var api = new FakeDriveApi();
        for (var i = 0; i < 10; i++)
        {
            api.Add($"f{i}", folder: true);
            api.Add($"f{i}/a.txt");
        }
        var engine = Create(api, maxCachedItems: 10);
        await engine.PollAsync("b!me");
        for (var i = 0; i < 10; i++)
        {
            await Names(engine, $"\\f{i}");
        }

        now += TimeSpan.FromMinutes(3);
        await Names(engine, "\\f9"); // in use: stays
        await engine.PollActiveAsync();
        var lists = api.ListCalls;
        await Names(engine, "\\f9");
        Assert.Equal(lists, api.ListCalls);
        await Names(engine, "\\f0");

        Assert.Equal(lists + 1, api.ListCalls);
        Assert.Contains(log, line => line.Contains("left memory"));
    }

    [Fact]
    public async Task The_metadata_version_changes_only_when_listings_do()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/a.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\docs");
        var version = engine.MetadataVersion;

        await engine.PollAsync("b!me");
        Assert.Equal(version, engine.MetadataVersion);

        api.Add("docs/b.txt");
        await engine.PollAsync("b!me");
        Assert.NotEqual(version, engine.MetadataVersion);
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
