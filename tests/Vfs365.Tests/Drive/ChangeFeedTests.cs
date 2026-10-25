using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

public sealed class ChangeFeedTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly List<EngineChange> changes = [];
    DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    readonly List<string> log = [];

    DriveEngine Create(IDriveApi api, int maxCachedItems = 250_000) =>
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

    /// <summary>Clients\Acme\2026\order.xlsx, where the user may list 2026 but not the folders above it (broken inheritance).</summary>
    static (FakeDriveApi Fake, StallingDriveApi Api) ClientFolders(bool listable)
    {
        var fake = new FakeDriveApi();
        fake.Add("Clients", folder: true);
        fake.Add("Clients/Acme", folder: true);
        fake.Add("Clients/Acme/2026", folder: true);
        fake.Add("Clients/Acme/2026/order.xlsx");
        var api = new StallingDriveApi(fake);
        if (!listable)
        {
            api.Unlistable.UnionWith(["Clients", "Clients/Acme"]);
        }
        return (fake, api);
    }

    [Fact]
    public async Task A_rename_of_a_folder_above_a_cached_listing_is_seen_when_that_folder_is_not_cached()
    {
        var (fake, api) = ClientFolders(listable: false);
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["order.xlsx"], await Names(engine, "\\Clients\\Acme\\2026"));

        // A colleague archives Acme and starts a new one
        fake.RemoteMove("Clients/Acme", "Clients/Acme (archive)");
        fake.Add("Clients/Acme", folder: true);
        await engine.PollAsync("b!me");

        Assert.Null(await engine.GetEntryAsync("\\Clients\\Acme\\2026\\order.xlsx", default));
        Assert.True(Saw("\\Clients\\Acme", ChangeKind.Removed));
    }

    [Fact]
    public async Task Below_a_folder_that_is_not_cached_a_listing_answers_only_briefly()
    {
        var (fake, api) = ClientFolders(listable: false);
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["order.xlsx"], await Names(engine, "\\Clients\\Acme\\2026"));

        // Two levels up, where nothing is cached: the feed can't tell the listing moved
        fake.RemoteMove("Clients", "Old clients");
        await engine.PollAsync("b!me");
        now += TimeSpan.FromSeconds(61);

        Assert.Null(await engine.GetEntryAsync("\\Clients\\Acme\\2026\\order.xlsx", default));
    }

    [Fact]
    public async Task A_folder_opened_by_path_gets_the_folders_above_it_looked_up_and_stays_current()
    {
        // Each folder above is read as one item, not listed: listing a big one would cost a page per thousand items
        var (fake, api) = ClientFolders(listable: true);
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.Equal(["order.xlsx"], await Names(engine, "\\Clients\\Acme\\2026"));
        await Eventually(() => fake.GetCalls == 3); // 2026 itself, then Acme and Clients in the background
        await Task.Delay(50);

        now += TimeSpan.FromHours(1);
        var gets = fake.GetCalls;
        Assert.NotNull(await engine.GetEntryAsync("\\Clients\\Acme\\2026\\order.xlsx", default));
        Assert.Equal(gets, fake.GetCalls); // kept current by the feed: no lookup

        fake.RemoteMove("Clients", "Old clients");
        await engine.PollAsync("b!me");

        Assert.Null(await engine.GetEntryAsync("\\Clients\\Acme\\2026\\order.xlsx", default));
        Assert.True(Saw("\\Clients", ChangeKind.Removed));
        Assert.Equal(["Clients/Acme/2026"], api.Listed);
    }

    [Fact]
    public async Task Folders_above_a_cached_listing_known_by_id_survive_a_restart()
    {
        var (fake, api) = ClientFolders(listable: true);
        var first = Create(api);
        await first.PollAsync("b!me");
        await Names(first, "\\Clients\\Acme\\2026");
        await Eventually(() => fake.GetCalls == 3);
        await Task.Delay(50);
        var snapshots = first.ExportSnapshots(100);
        Assert.Equal(["Clients", "Clients/Acme"], snapshots.Single().Ancestors!.Select(a => a.Path).Order(StringComparer.Ordinal));

        var second = Create(api);
        second.ImportSnapshots(snapshots);
        await second.PollAsync("b!me");
        fake.RemoteMove("Clients/Acme", "Clients/Acme (archive)");
        await second.PollAsync("b!me");

        Assert.True(Saw("\\Clients\\Acme", ChangeKind.Removed));
        Assert.Null(await second.GetEntryAsync("\\Clients\\Acme\\2026\\order.xlsx", default));
    }

    [Fact]
    public async Task A_listing_whose_refresh_finds_the_folder_gone_is_dropped()
    {
        var fake = new FakeDriveApi { DeltaUnavailable = true };
        fake.Add("docs", folder: true);
        fake.Add("docs/sub", folder: true);
        fake.Add("docs/sub/a.txt");
        var engine = Create(new StallingDriveApi(fake)); // answers 404 for a folder that isn't there, as Graph does
        var sub = (await engine.GetEntryAsync("\\docs\\sub", default))!;
        Assert.Equal(["a.txt"], (await engine.ListAsync(sub, default)).Select(e => e.Name));

        fake.RemoteDelete("docs/sub"); // never seen: the feed doesn't work
        now += TimeSpan.FromMinutes(2);
        var stale = await engine.ListAsync(sub, default);
        await Eventually(() => Saw("\\docs\\sub", ChangeKind.Removed));

        Assert.Equal(["a.txt"], stale.Select(e => e.Name));
        var gone = await Assert.ThrowsAsync<Vfs365.Core.RemoteException>(() => engine.ListAsync(sub, default));
        Assert.Equal(Vfs365.Core.RemoteError.NotFound, gone.Error);
    }

    [Fact]
    public async Task A_file_opened_after_the_drive_was_idle_is_the_version_now_in_Microsoft_365()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/a.txt", content: "one");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\docs");

        now += TimeSpan.FromMinutes(15); // nothing read the feed meanwhile
        api.RemoteEdit("docs/a.txt", "two two");

        Assert.Equal(7, (await engine.GetEntryAsync("\\docs\\a.txt", default))!.Size);
    }

    [Fact]
    public async Task A_file_opened_right_after_a_restart_is_the_version_now_in_Microsoft_365()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/a.txt", content: "one");
        var first = Create(api);
        await first.PollAsync("b!me");
        await Names(first, "\\");
        await Names(first, "\\docs");
        var snapshots = first.ExportSnapshots(100);

        api.RemoteEdit("docs/a.txt", "two two");
        var second = Create(api);
        second.ImportSnapshots(snapshots);

        Assert.Equal(7, (await second.GetEntryAsync("\\docs\\a.txt", default))!.Size);
    }

    [Fact]
    public async Task A_file_saved_while_its_folder_loads_stays_in_the_listing()
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

        await engine.CreateAsync("\\docs\\new.txt", false, default);
        await engine.CommitAsync("\\docs\\new.txt", default); // the listing's pages were read before
        rest.SetResult();
        await loading;
        var lists = api.ListCalls;

        Assert.Contains("new.txt", await Names(engine, "\\docs"));
        Assert.Equal(lists, api.ListCalls);
    }

    [Fact]
    public async Task A_file_saved_while_its_folder_is_read_again_stays_in_the_listing()
    {
        var api = new FakeDriveApi { PageSize = 2 };
        api.Add("docs", folder: true);
        for (var i = 1; i <= 5; i++)
        {
            api.Add($"docs/f{i}.txt");
        }
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\docs");
        api.AddUnnoticed("docs/elsewhere.txt"); // only the refresh brings this one
        engine.ExpireListings();
        var (reading, rest) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource());
        api.BeforeNextPage = () =>
        {
            reading.TrySetResult();
            return rest.Task;
        };

        await Names(engine, "\\docs"); // answers from the stale listing; the refresh holds after its first page
        await reading.Task;
        await engine.CreateAsync("\\docs\\new.txt", false, default);
        await engine.CommitAsync("\\docs\\new.txt", default);
        rest.SetResult();
        await Eventually(() => Saw("\\docs\\elsewhere.txt", ChangeKind.Added));

        Assert.Contains("new.txt", await Names(engine, "\\docs"));
        Assert.False(Saw("\\docs\\new.txt", ChangeKind.Removed));
    }

    [Fact]
    public async Task A_change_read_before_a_save_does_not_put_back_the_older_version()
    {
        var fake = new FakeDriveApi();
        fake.Add("docs", folder: true);
        fake.Add("docs/a.txt", content: "v1");
        var api = new StallingDriveApi(fake) { GraphTags = true };
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\docs");
        await SaveAsync(engine, "\\docs\\a.txt", "v2");

        // A feed read that brings v2 back is held until v3 is saved
        var (read, held) = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource());
        api.AfterDeltaRead = () =>
        {
            read.TrySetResult();
            return held.Task;
        };
        var polling = engine.PollAsync("b!me");
        await read.Task;
        api.AfterDeltaRead = null;
        await SaveAsync(engine, "\\docs\\a.txt", "v3");
        held.SetResult();
        await polling;
        await SaveAsync(engine, "\\docs\\a.txt", "v4");

        Assert.Equal("v4", fake.ContentOf("docs/a.txt"));
        Assert.DoesNotContain(fake.Paths, path => path.Contains("conflict"));
    }

    static async Task SaveAsync(DriveEngine engine, string path, string text)
    {
        var entry = (await engine.GetEntryAsync(path, default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: true, default);
        await File.WriteAllTextAsync(staging, text);
        engine.MarkDirty((await engine.GetEntryAsync(path, default))!);
        await engine.CommitAsync(path, default);
    }

    [Fact]
    public async Task A_drive_whose_change_feed_fails_gets_no_push_channel()
    {
        var api = new FakeDriveApi { DeltaUnavailable = true };
        api.Add("docs", folder: true);
        var engine = Create(api);

        await Names(engine, "\\docs");
        await Eventually(() =>
        {
            lock (log)
            {
                return log.Any(line => line.Contains("change feed of"));
            }
        });

        Assert.Equal(["b!me"], engine.ActiveDrives());
        Assert.Empty(engine.PushDrives());
    }

    [Fact]
    public async Task Deleting_or_renaming_a_subfolder_keeps_its_folder_listed()
    {
        var api = new FakeDriveApi();
        api.Add("big", folder: true);
        for (var i = 0; i < 5; i++)
        {
            api.Add($"big/f{i}.txt");
        }
        api.Add("big/old", folder: true);
        api.Add("big/sub", folder: true);
        api.Add("big/sub/x.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\big");
        Assert.Equal(["x.txt"], await Names(engine, "\\big\\sub"));
        var (lists, gets) = (api.ListCalls, api.GetCalls);

        await engine.DeleteAsync("\\big\\old", default);
        await engine.RenameAsync("\\big\\sub", "\\big\\renamed", replace: false, default);

        // Before, each of the two listed \big again (a page per thousand items)
        Assert.Equal(["f0.txt", "f1.txt", "f2.txt", "f3.txt", "f4.txt", "renamed"], await Names(engine, "\\big"));
        Assert.Equal(lists, api.ListCalls);
        Assert.Null(await engine.GetEntryAsync("\\big\\sub\\x.txt", default));
        Assert.Equal(gets, api.GetCalls);
        Assert.Equal(["x.txt"], await Names(engine, "\\big\\renamed"));
    }

    [Fact]
    public async Task Reading_a_stale_listing_again_reads_only_that_folder()
    {
        var api = new FakeDriveApi { DeltaUnavailable = true, PageSize = 2 };
        foreach (var folder in new[] { "a", "a/b", "a/b/c" })
        {
            api.Add(folder, folder: true);
            for (var i = 0; i < 4; i++)
            {
                api.Add($"{folder}/f{i}.txt");
            }
        }
        var engine = Create(api);
        await Names(engine, "\\a");
        await Names(engine, "\\a\\b");
        await Names(engine, "\\a\\b\\c");
        now += TimeSpan.FromMinutes(2); // all stale: the feed doesn't work
        var (pages, gets) = (api.ListPages, api.GetCalls);

        Assert.NotNull(await engine.GetEntryAsync("\\a\\b\\c\\f1.txt", default));
        await Eventually(() => api.ListPages >= pages + 2);
        await Task.Delay(100);

        // c's 4 items in pages of 2; before, b and a were read again too (6 pages more)
        Assert.Equal(pages + 2, api.ListPages);
        Assert.Equal(gets + 2, api.GetCalls); // the file, and c itself for its ID
    }

    [Fact]
    public async Task Cached_folders_deleted_children_first_all_go()
    {
        static DriveItemInfo Folder(string id, string name) => new(id, name, true, 0, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, $"e-{id}", null);
        var subs = Enumerable.Range(0, 300).Select(i => Folder($"s{i}", $"s{i:D3}")).ToList();
        var folders = new List<FolderSnapshot> { new("", "root", [Folder("top", "top")], now), new("top", "top", subs, now, "root") };
        folders.AddRange(subs.Select(s => new FolderSnapshot($"top/{s.Name}", s.Id, [Folder($"{s.Id}-x", "x")], now, "top")));
        var api = new StallingDriveApi(new FakeDriveApi());
        var engine = Create(api);
        engine.ImportSnapshots([new DriveSnapshot("b!me", "delta:0", now, folders)]);
        api.Scripted.Enqueue(new DeltaPage([.. subs.Select(s => new DriveChange(s.Id, "top", null, true)), new DriveChange("top", "root", null, true)], null, "delta:0"));

        await engine.PollAsync("b!me");

        Assert.Empty(await Names(engine, "\\"));
        Assert.True(Saw("\\top", ChangeKind.Removed));
        Assert.Equal([""], engine.ExportSnapshots(1000).Single().Folders.Select(f => f.Path));
    }

    [Fact]
    public async Task After_a_week_away_a_catch_up_still_running_does_not_make_old_listings_current()
    {
        var fake = new FakeDriveApi();
        fake.Add("docs", folder: true);
        fake.Add("docs/a.txt", content: "one");
        var api = new StallingDriveApi(fake);
        var first = Create(api);
        await first.PollAsync("b!me");
        await Names(first, "\\");
        await Names(first, "\\docs");
        var snapshots = first.ExportSnapshots(100);

        now += TimeSpan.FromDays(7);
        fake.RemoteEdit("docs/a.txt", "two two");
        var held = new TaskCompletionSource();
        api.AfterDeltaRead = () => held.Task; // hundreds of pages to catch up on
        var second = Create(api);
        second.ImportSnapshots(snapshots);
        var gets = fake.GetCalls;

        var entry = await second.GetEntryAsync("\\docs\\a.txt", default); // waits briefly for the catch-up, then asks
        held.SetResult();

        Assert.Equal(7, entry!.Size);
        Assert.Equal(gets + 1, fake.GetCalls);
    }

    [Fact]
    public async Task The_metadata_version_ignores_what_the_snapshot_does_not_keep()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        api.Add("docs/sub", folder: true);
        api.Add("docs/sub/a.txt");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\docs");
        var version = engine.MetadataVersion;

        Assert.NotNull(await engine.GetEntryAsync("\\docs\\sub\\a.txt", default)); // a lookup
        api.RemoteMetadataEdit("docs/sub"); // what a change below \docs\sub does to it: a new eTag and size
        await engine.PollAsync("b!me");
        Assert.Equal(version, engine.MetadataVersion);
        Assert.False(Saw("\\docs\\sub", ChangeKind.Modified)); // nor is Explorer told to read \docs again

        // A drive whose feed doesn't work keeps nothing
        var broken = new FakeDriveApi { DeltaUnavailable = true };
        broken.Add("docs", folder: true);
        var other = Create(broken);
        var before = other.MetadataVersion;
        await Names(other, "\\docs");
        await other.CreateAsync("\\docs\\new.txt", false, default);
        Assert.Equal(before, other.MetadataVersion);

        api.Add("docs/b.txt");
        await engine.PollAsync("b!me");
        Assert.NotEqual(version, engine.MetadataVersion);
    }

    [Fact]
    public async Task A_catch_up_that_fails_midway_goes_on_from_the_page_that_failed()
    {
        var fake = new FakeDriveApi { DeltaPageSize = 1 };
        fake.Add("docs", folder: true);
        var api = new StallingDriveApi(fake);
        var engine = Create(api);
        await engine.PollAsync("b!me");
        await Names(engine, "\\docs");
        for (var i = 0; i < 6; i++)
        {
            fake.Add($"docs/f{i}.txt");
        }
        var reads = 0;
        api.AfterDeltaRead = () => ++reads == 3 ? throw new HttpRequestException("connection reset") : Task.CompletedTask;

        await engine.PollAsync("b!me"); // pages 1 and 2 are applied, the third fails
        Assert.Equal(["f0.txt", "f1.txt"], await Names(engine, "\\docs"));
        var calls = api.DeltaCalls;
        now += TimeSpan.FromMinutes(2); // past the back-off
        await engine.PollAsync("b!me");

        Assert.Equal(calls + 4, api.DeltaCalls); // pages 3 to 6, not all 6 again
        Assert.Equal(6, (await Names(engine, "\\docs")).Length);
    }

    [Fact]
    public async Task A_file_looked_up_by_path_shows_a_remote_change_after_the_next_read()
    {
        var api = new FakeDriveApi();
        api.Add("a", folder: true);
        api.Add("a/b", folder: true);
        api.Add("a/b/report.txt", content: "one");
        var engine = Create(api);
        await engine.PollAsync("b!me");
        Assert.NotNull(await engine.GetEntryAsync("\\a\\b", default));
        Assert.Equal(3, (await engine.GetEntryAsync("\\a\\b\\report.txt", default))!.Size);
        Assert.Null(await engine.GetEntryAsync("\\a\\b\\new.txt", default));

        // Their folder isn't cached; before, the lookups answered as they were until they expired
        api.RemoteEdit("a/b/report.txt", "two two");
        api.Add("a/b/new.txt");
        await engine.PollAsync("b!me");

        Assert.Equal(7, (await engine.GetEntryAsync("\\a\\b\\report.txt", default))!.Size);
        Assert.NotNull(await engine.GetEntryAsync("\\a\\b\\new.txt", default));
    }

    [Fact]
    public async Task A_lookup_in_a_folder_still_loading_uses_what_arrived()
    {
        var api = new FakeDriveApi { PageSize = 2 };
        api.Add("docs", folder: true);
        for (var i = 1; i <= 5; i++)
        {
            api.Add($"docs/f{i}.txt");
        }
        var engine = Create(api);
        var rest = new TaskCompletionSource();
        api.BeforeNextPage = () => rest.Task;
        var docs = (await engine.GetEntryAsync("\\docs", default))!;
        await using var batches = engine.ListStreamAsync(docs, default).GetAsyncEnumerator();
        Assert.True(await batches.MoveNextAsync()); // the first page is in
        var gets = api.GetCalls;

        Assert.NotNull(await engine.GetEntryAsync("\\docs\\f1.txt", default));

        Assert.Equal(gets, api.GetCalls);
        rest.SetResult();
    }

    [Fact]
    public async Task A_library_no_longer_shown_leaves_the_snapshot()
    {
        static LibraryEntry Library(string site) => new($"k-{site}", "s", "w", "l", $"https://x.sharepoint.com/sites/{site}", site, "Documents", false, 1, $"b!{site}");
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [Library("Finance"), Library("HR")], (_, _) => Task.FromResult("unused"));
        var engine = new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), Clock = () => now });
        foreach (var site in new[] { "Finance", "HR" })
        {
            await engine.PollAsync($"b!{site}");
            await engine.ListAsync((await engine.GetEntryAsync($"\\Sites\\{site}\\Documents\\docs", default))!, default);
        }
        Assert.Equal(["b!Finance", "b!HR"], engine.ExportSnapshots(100).Select(s => s.DriveId).Order(StringComparer.Ordinal));

        engine.SetLibraries([Library("Finance")]);

        Assert.Equal(["b!Finance"], engine.ExportSnapshots(100).Select(s => s.DriveId));
        Assert.Equal(["b!Finance"], engine.ActiveDrives());
    }

    [Fact]
    public async Task A_restored_library_only_glanced_at_starts_no_change_feed()
    {
        var api = new FakeDriveApi();
        api.Add("docs", folder: true);
        var first = Create(api);
        await first.PollAsync("b!me");
        await Names(first, "\\");
        await Names(first, "\\docs");
        var snapshots = first.ExportSnapshots(100);
        var second = Create(api);
        second.ImportSnapshots(snapshots);
        var calls = api.DeltaCalls;

        await Names(second, "\\"); // Explorer drawing the library's thumbnail

        Assert.Equal(calls, api.DeltaCalls);
        Assert.Empty(second.ActiveDrives());
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
