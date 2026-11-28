using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>What folder listings below a folder cost, for the budget of Explorer's own search (ExplorerSearch Allow).</summary>
public sealed class ListingMeterTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new() { DeltaUnavailable = true }; // no change feed, whose baseline reads the folders once more

    DriveEngine Create() =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), WalkThreshold = 1000 });

    async Task List(DriveEngine engine, string path) => await engine.ListAsync((await engine.GetEntryAsync(path, default))!, default);

    [Fact]
    public async Task Listings_read_from_Microsoft_365_count_below_the_folder_measured_only()
    {
        api.Add("Projects", folder: true);
        api.Add("Projects/2026", folder: true);
        api.Add("Projects/2026/a.txt");
        api.Add("Projectsold", folder: true);
        api.Add("Other", folder: true);
        var engine = Create();
        using var meter = engine.MeasureListings(@"\Projects\");

        await List(engine, @"\Projects");
        await List(engine, @"\Projects\2026");
        await List(engine, @"\Projectsold"); // the same start, another folder
        await List(engine, @"\Other");
        Assert.Equal(2 * DriveEngine.ListingPageUnits, meter.Units);

        await List(engine, @"\Projects\2026"); // cached: no cost
        Assert.Equal(2 * DriveEngine.ListingPageUnits, meter.Units);
    }

    [Fact]
    public async Task Every_page_counts_and_a_disposed_meter_stops()
    {
        api.PageSize = 2;
        api.Add("Big", folder: true);
        for (var i = 0; i < 5; i++)
        {
            api.Add($"Big/f{i}.txt");
        }
        api.Add("Later", folder: true);
        var engine = Create();
        var meter = engine.MeasureListings(@"\");

        await List(engine, @"\Big");
        Assert.Equal(3 * DriveEngine.ListingPageUnits, meter.Units);

        meter.Dispose();
        await List(engine, @"\Later");
        Assert.Equal(3 * DriveEngine.ListingPageUnits, meter.Units);
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
