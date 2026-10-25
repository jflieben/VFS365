using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

public sealed class DriveEngineTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));

    static LibraryEntry Library(string site, string title, string webUrl) =>
        new($"k-{site}-{title}", "s", "w", "l", webUrl, site, title, false, 1);

    (DriveEngine Engine, FakeDriveApi Api, Func<int> Resolves) Create(params LibraryEntry[] libraries)
    {
        var api = new FakeDriveApi();
        var resolves = 0;
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"),
            libraries.Length > 0 ? libraries : [Library("Finance", "Documents", "https://x.sharepoint.com/sites/Finance")],
            (_, _) => { Interlocked.Increment(ref resolves); return Task.FromResult("b!finance"); });
        var options = new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") };
        return (new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api), options), api, () => resolves);
    }

    [Fact]
    public async Task Virtual_tree_needs_no_calls()
    {
        var (engine, api, resolves) = Create();

        var root = (await engine.GetEntryAsync("\\", default))!;
        var top = await engine.ListAsync(root, default);
        var library = await engine.GetEntryAsync("\\sites\\FINANCE\\documents", default);

        Assert.Equal(["OneDrive", "Sites"], top.Select(e => e.Name).Order());
        Assert.Equal("\\Sites\\Finance\\Documents", library!.Path);
        Assert.True(library.IsDirectory);
        Assert.Equal(0, api.ListCalls + api.GetCalls + resolves());
    }

    DriveEngine Scoped(DriveScope scope, FakeDriveApi api)
    {
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"),
            [Library("Finance", "Documents", "https://x.sharepoint.com/sites/Finance"), Library("Finance", "Archive", "https://x.sharepoint.com/sites/Finance")],
            (_, _) => Task.FromResult("b!finance"), scope);
        return new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });
    }

    [Fact]
    public async Task OneDrive_only_is_the_root()
    {
        var api = new FakeDriveApi();
        api.Add("notes.txt");
        var engine = Scoped(DriveScope.OneDrive, api);

        var root = (await engine.GetEntryAsync("\\", default))!;
        var top = await engine.ListAsync(root, default);
        var created = await engine.CreateAsync("\\new.txt", false, default);

        Assert.Equal(["notes.txt"], top.Select(e => e.Name));
        Assert.NotNull(await engine.GetEntryAsync("\\notes.txt", default));
        Assert.NotNull(created.StagingPath);
        Assert.Null(await engine.GetEntryAsync("\\Sites", default));
    }

    [Fact]
    public async Task SharePoint_only_shows_sites_at_the_root()
    {
        var engine = Scoped(DriveScope.SharePoint, new FakeDriveApi());

        var top = await engine.ListAsync((await engine.GetEntryAsync("\\", default))!, default);
        var libraries = await engine.ListAsync((await engine.GetEntryAsync("\\Finance", default))!, default);

        Assert.Equal(["Finance"], top.Select(e => e.Name));
        Assert.Equal(["Archive", "Documents"], libraries.Select(e => e.Name).Order());
        Assert.Null(await engine.GetEntryAsync("\\OneDrive", default));
        await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync("\\stray.txt", false, default));
    }

    [Fact]
    public async Task Deep_path_in_unlisted_folder_costs_one_lookup_and_is_cached()
    {
        var (engine, api, resolves) = Create();
        api.Add("a/b/c/report.pdf");

        var entry = await engine.GetEntryAsync("\\Sites\\Finance\\Documents\\a\\b\\c\\report.pdf", default);
        await engine.GetEntryAsync("\\Sites\\Finance\\Documents\\a\\b\\c\\report.pdf", default);

        Assert.Equal("report.pdf", entry!.Name);
        Assert.Equal(1, api.GetCalls);
        Assert.Equal(1, resolves());
    }

    [Fact]
    public async Task Listed_folder_answers_lookups_including_not_found()
    {
        var (engine, api, _) = Create();
        api.Add("a", folder: true);
        api.Add("a/one.txt");

        var folder = (await engine.GetEntryAsync("\\OneDrive\\a", default))!;
        var children = await engine.ListAsync(folder, default);
        await engine.ListAsync(folder, default);
        var one = await engine.GetEntryAsync("\\OneDrive\\a\\ONE.txt", default);
        var missing = await engine.GetEntryAsync("\\OneDrive\\a\\desktop.ini", default);

        Assert.Single(children);
        Assert.Equal("\\OneDrive\\a\\one.txt", children[0].Path);
        Assert.NotNull(one);
        Assert.Null(missing);
        Assert.Equal(1, api.ListCalls);
        Assert.Equal(1, api.GetCalls);
    }

    [Fact]
    public async Task Many_files_opened_by_path_in_an_unlisted_folder_get_the_folder_listed()
    {
        var (engine, api, _) = Create();
        api.Add("scans", folder: true);
        for (var i = 0; i < 10; i++)
        {
            api.Add($"scans/{i:D3}.pdf");
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.NotNull(await engine.GetEntryAsync($"\\OneDrive\\scans\\{i:D3}.pdf", default));
        }
        // The fifth started loading the folder; listing it joins that load
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive\\scans", default))!, default);
        var gets = api.GetCalls;
        for (var i = 5; i < 10; i++)
        {
            Assert.NotNull(await engine.GetEntryAsync($"\\OneDrive\\scans\\{i:D3}.pdf", default));
        }

        Assert.Equal(1, api.ListCalls);
        Assert.Equal(gets, api.GetCalls);
    }

    [Fact]
    public async Task Missing_path_is_remembered()
    {
        var (engine, api, _) = Create();

        Assert.Null(await engine.GetEntryAsync("\\OneDrive\\x\\y.txt", default));
        Assert.Null(await engine.GetEntryAsync("\\OneDrive\\x\\y.txt", default));
        Assert.Null(await engine.GetEntryAsync("\\Nope", default));

        Assert.Equal(1, api.GetCalls);
    }

    [Fact]
    public async Task Content_is_downloaded_once()
    {
        var (engine, api, _) = Create();
        api.Add("doc.txt");
        var file = (await engine.GetEntryAsync("\\OneDrive\\doc.txt", default))!;

        var texts = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => ReadAllAsync(engine, file)));
        var again = await ReadAllAsync(engine, file);

        Assert.Equal(1, api.Downloads);
        Assert.All(texts, text => Assert.Equal("abc", text));
        Assert.Equal("abc", again);
    }

    static async Task<string> ReadAllAsync(DriveEngine engine, FsEntry file)
    {
        using var source = await engine.OpenContentAsync(file, default);
        var buffer = new byte[source.Length];
        var read = await source.ReadAsync(0, buffer, default);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    [Theory]
    [InlineData("👌Test Team 11/2024", "👌Test Team 11-2024")]
    [InlineData("a:b*c?", "a-b-c-")]
    [InlineData(" Trailing dots... ", "Trailing dots")]
    [InlineData("///", "---")]
    [InlineData("CON", "CON_")]
    [InlineData("nul", "nul_")]
    [InlineData("Com1.Team", "Com1_.Team")]
    [InlineData("LPT\u00B9", "LPT\u00B9_")]
    [InlineData("Console", "Console")]
    public void Site_and_library_names_are_made_windows_safe(string title, string expected)
    {
        Assert.Equal(expected, DriveNamespace.SafeName(title));
    }

    [Fact]
    public async Task Same_titled_sites_are_told_apart_by_their_url()
    {
        var (engine, _, _) = Create(
            Library("SubSite", "Documents", "https://x.sharepoint.com/sites/a"),
            Library("SUBSITE", "Documents", "https://x.sharepoint.com/sites/b"),
            Library("Projects", "Documents", "https://x.sharepoint.com/sites/team/projects") with { Key = "team" },
            Library("Projects", "Documents", "https://x.sharepoint.com/teams/other/projects") with { Key = "other" });

        var sites = await engine.ListAsync((await engine.GetEntryAsync("\\Sites", default))!, default);

        Assert.Equal(["Projects (sites-team-projects)", "Projects (teams-other-projects)", "SubSite (a)", "SUBSITE (b)"], sites.Select(s => s.Name));
    }

    [Fact]
    public async Task Same_titled_sites_keep_their_names_whatever_order_discovery_finds_them_in()
    {
        // Two "Projects" sites; then one gains a library, which moves it to the end of the search answer
        var us = Library("Projects", "Documents", "https://x.sharepoint.com/sites/projects-us") with { Key = "us-docs" };
        var eu = Library("Projects", "Documents", "https://x.sharepoint.com/sites/projects-eu") with { Key = "eu-docs" };
        var euArchive = Library("Projects", "Archive", "https://x.sharepoint.com/sites/projects-eu") with { Key = "eu-archive" };
        var changes = new List<EngineChange>();
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [us, eu], (library, _) => Task.FromResult("b!" + library.WebUrl[^2..]));
        var engine = new DriveEngine(ns, new FakeDriveApi(), new ContentCache(Path.Combine(directory, "cache"), new FakeDriveApi()),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), Changed = changes.Add });
        var usFolder = (await engine.GetEntryAsync("\\Sites\\Projects (projects-us)\\Documents", default))!;
        Assert.Equal("b!us", await engine.DriveIdOfAsync(usFolder, default));

        engine.SetLibraries([eu, euArchive, us]);
        engine.SetLibraries([euArchive, us, eu]);

        Assert.Equal("b!us", await engine.DriveIdOfAsync((await engine.GetEntryAsync("\\Sites\\Projects (projects-us)\\Documents", default))!, default));
        Assert.Equal("b!eu", await engine.DriveIdOfAsync((await engine.GetEntryAsync("\\Sites\\Projects (projects-eu)\\Documents", default))!, default));
        Assert.Null(await engine.GetEntryAsync("\\Sites\\Projects", default));
        Assert.Equal(["\\Sites\\Projects (projects-eu)"], changes.Select(c => c.Path).Distinct());
    }

    [Fact]
    public void A_name_that_now_shows_another_site_is_reported_as_changed()
    {
        var first = Library("Projects", "Documents", "https://x.sharepoint.com/sites/projects-us") with { Key = "us-docs" };
        var second = Library("Projects", "Documents", "https://x.sharepoint.com/sites/projects-eu") with { Key = "eu-docs" };
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [first], (_, _) => Task.FromResult("b!x"));

        // The same names, another site behind them: \Sites keeps its child names, yet what \Sites\Projects shows changed
        var changed = ns.SetLibraries([second]);

        Assert.Equal(["\\Sites", "\\Sites\\Projects", "\\Sites\\Projects\\Documents"], changed.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_known_drive_needs_no_lookup_to_find_where_it_is_mounted()
    {
        var resolves = 0;
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"),
            [Library("Finance", "Documents", "https://x.sharepoint.com/sites/Finance") with { DriveId = "b!finance" }],
            (_, _) => { resolves++; return Task.FromResult("b!other"); });

        Assert.Equal("\\Sites\\Finance\\Documents", ns.VolumePathOf("b!finance"));
        Assert.Equal("\\OneDrive", ns.VolumePathOf("b!me"));
        Assert.Null(ns.VolumePathOf("b!gone"));
        var engine = new DriveEngine(ns, new FakeDriveApi(), new ContentCache(Path.Combine(directory, "cache"), new FakeDriveApi()),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });
        Assert.Equal("b!finance", await engine.DriveIdOfAsync((await engine.GetEntryAsync("\\Sites\\Finance\\Documents", default))!, default));
        Assert.Equal(0, resolves);
    }

    [Fact]
    public async Task Listings_come_sorted_and_scans_of_a_big_folder_reuse_their_entries()
    {
        var (engine, api, _) = Create();
        api.Add("big", folder: true);
        foreach (var i in Enumerable.Range(0, 600).Reverse())
        {
            api.Add($"big/F{i:D3}.txt");
        }
        api.Add("big/a.txt");
        var folder = (await engine.GetEntryAsync("\\OneDrive\\big", default))!;

        var first = await engine.ListAsync(folder, default); // while it loads: page by page, then sorted
        var second = await engine.ListAsync(folder, default);
        var third = await engine.ListAsync(folder, default);

        Assert.Equal(first.Select(e => e.Name).Order(StringComparer.OrdinalIgnoreCase), first.Select(e => e.Name));
        Assert.Equal(first.Select(e => e.Name), second.Select(e => e.Name));
        Assert.Same(second, third); // nothing built again
        Assert.Equal("a.txt", third[0].Name);

        await engine.CreateAsync("\\OneDrive\\big\\b.txt", false, default); // local state goes in, in order
        var fourth = await engine.ListAsync(folder, default);
        Assert.Equal(["a.txt", "b.txt", "F000.txt"], fourth.Take(3).Select(e => e.Name));
        Assert.Equal(1, api.ListCalls);
    }

    [Fact]
    public async Task Nothing_is_looked_up_below_a_folder_known_not_to_be_there()
    {
        var (engine, api, _) = Create();
        api.Add("docs", folder: true);
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        var gets = api.GetCalls;

        Assert.Null(await engine.GetEntryAsync("\\OneDrive\\nope\\desktop.ini", default));
        Assert.Null(await engine.GetEntryAsync("\\OneDrive\\nope\\a\\b\\c.txt", default));

        Assert.Equal(gets, api.GetCalls);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
