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
    public void Site_and_library_names_are_made_windows_safe(string title, string expected)
    {
        Assert.Equal(expected, DriveNamespace.SafeName(title));
    }

    [Fact]
    public async Task Duplicate_site_names_get_a_number()
    {
        var (engine, _, _) = Create(
            Library("SubSite", "Documents", "https://x.sharepoint.com/sites/a"),
            Library("SUBSITE", "Documents", "https://x.sharepoint.com/sites/b"));

        var sites = await engine.ListAsync((await engine.GetEntryAsync("\\Sites", default))!, default);

        Assert.Equal(["SUBSITE (2)", "SubSite"], sites.Select(s => s.Name).Order(StringComparer.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
