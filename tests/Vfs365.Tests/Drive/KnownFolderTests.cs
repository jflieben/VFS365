using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>
/// Folders above a listing, known by ID, let the change feed keep the listing current; one read before the feed's baseline doesn't
/// count and is looked up again, so the listing isn't read again every ListingTtl for the rest of the run.
/// </summary>
public sealed class KnownFolderTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    static FakeDriveApi Tree()
    {
        var api = new FakeDriveApi();
        api.Add("A", folder: true);
        api.Add("A/B", folder: true);
        api.Add("A/B/C", folder: true);
        api.Add("A/B/C/f.txt");
        return api;
    }

    /// <summary>Listings of C in 5 uses 90 s apart, with the feed read before each.</summary>
    async Task<int> ListsOverTime(DriveEngine engine, FakeDriveApi api, FsEntry c)
    {
        var start = api.ListCalls;
        for (var i = 0; i < 5; i++)
        {
            now += TimeSpan.FromSeconds(90);
            await engine.PollAsync("b!me");
            await engine.ListAsync(c, default);
            await Task.Delay(100);
        }
        return api.ListCalls - start;
    }

    async Task<(DriveEngine Engine, FakeDriveApi Api, FsEntry C)> OpenC(bool earlyLookup)
    {
        var api = Tree();
        var engine = ReadPathTesting.Create(directory, api, () => now);
        if (earlyLookup)
        {
            await engine.GetEntryAsync(@"\A", default); // Explorer resolving the path before the drive's feed has a baseline
        }
        now += TimeSpan.FromSeconds(1);
        await engine.PollAsync("b!me"); // baseline
        now += TimeSpan.FromSeconds(1);
        var c = (await engine.GetEntryAsync(@"\A\B\C", default))!;
        await engine.ListAsync(c, default);
        await ReadPathTesting.Eventually(() => api.LookedUp.Count(p => p == "A/B") >= 1);
        await Task.Delay(200);
        return (engine, api, c);
    }

    [Fact]
    public async Task A_listing_below_folders_looked_up_after_the_baseline_stays_current()
    {
        var (engine, api, c) = await OpenC(earlyLookup: false);
        Assert.Equal(0, await ListsOverTime(engine, api, c));
    }

    [Fact]
    public async Task A_folder_looked_up_before_the_baseline_is_looked_up_again_and_the_listing_below_stays_current()
    {
        var (engine, api, c) = await OpenC(earlyLookup: true);

        Assert.Equal(2, api.LookedUp.Count(p => p == "A"));
        Assert.Equal(0, await ListsOverTime(engine, api, c));
        Assert.Contains("A/B/C", engine.ExportSnapshots(100).Single().Folders.Select(f => f.Path));
    }

    [Fact]
    public async Task Folders_looked_up_while_a_new_baseline_failed_are_looked_up_again_once_it_works()
    {
        var fake = Tree();
        var api = new FreshItemsApi(fake);
        var engine = ReadPathTesting.Create(directory, api, () => now);
        await engine.PollAsync("b!me");
        fake.ExpireDeltaLinks = true;
        api.FailBaselines = 1;
        now += TimeSpan.FromSeconds(1);
        await engine.PollAsync("b!me"); // 410: what is cached goes, and the new baseline fails
        fake.ExpireDeltaLinks = false;

        now += TimeSpan.FromSeconds(1);
        var c = (await engine.GetEntryAsync(@"\A\B\C", default))!;
        await engine.ListAsync(c, default);
        await ReadPathTesting.Eventually(() => fake.LookedUp.Contains("A"));
        await Task.Delay(200);
        now += TimeSpan.FromMinutes(2); // past the back-off
        await engine.PollAsync("b!me"); // the baseline works now; A and A/B were looked up before it

        // C was listed before the baseline too: read once more, then kept current by the feed
        Assert.Equal(1, await ListsOverTime(engine, fake, c));
        Assert.Contains("A/B/C", engine.ExportSnapshots(100).Single().Folders.Select(f => f.Path));
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
