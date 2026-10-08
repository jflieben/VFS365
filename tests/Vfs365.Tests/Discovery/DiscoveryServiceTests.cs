using Vfs365.Core.Discovery;

namespace Vfs365.Tests.Discovery;

public class DiscoveryServiceTests
{
    const string Root = "https://contoso.sharepoint.com";

    sealed class FakeApi : ISharePointDiscoveryApi
    {
        public string OneDriveUrl = "https://contoso-my.sharepoint.com/personal/jos_contoso_com/Documents";
        public List<DiscoveredLibrary> Hits = [];
        public bool FailSearch;
        public HashSet<string> FailingSites = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SiteMetadata> Sites = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ListMetadata> Lists = new(StringComparer.OrdinalIgnoreCase);
        public int ListCalls;

        public Task<MyDrive> GetMyDriveAsync(CancellationToken ct) => Task.FromResult(new MyDrive("b!me", OneDriveUrl));

        public Task<SearchPage> SearchDocumentLibrariesAsync(string tenantRoot, int startRow, int rowLimit, CancellationToken ct) =>
            FailSearch ? throw new HttpRequestException("search down") : Task.FromResult(new SearchPage(Hits.Skip(startRow).Take(rowLimit).ToList(), Math.Min(rowLimit, Math.Max(0, Hits.Count - startRow))));

        public Task<SiteMetadata> GetSiteAsync(string webUrl, CancellationToken ct) =>
            FailingSites.Contains(webUrl) ? throw new HttpRequestException("site down") : Task.FromResult(Sites.GetValueOrDefault(webUrl, new SiteMetadata(false, false)));

        public Task<ListMetadata?> GetListAsync(string baseUrl, string listId, CancellationToken ct)
        {
            Interlocked.Increment(ref ListCalls);
            return Task.FromResult(Lists.GetValueOrDefault($"{baseUrl}|{listId}"));
        }

        public Task<string> GetLibraryDriveIdAsync(LibraryEntry library, CancellationToken ct) => Task.FromResult("b!drive");

        public Dictionary<string, PinnedLocation> Pins = new(StringComparer.OrdinalIgnoreCase);
        public bool FailPins;
        public int Resolves;

        public Task<PinnedLocation?> ResolveLocationAsync(string url, CancellationToken ct)
        {
            Interlocked.Increment(ref Resolves);
            return FailPins ? throw new HttpRequestException("site down") : Task.FromResult(Pins.GetValueOrDefault(url));
        }

        public void Add(string site, string listId, string title, ListMetadata? metadata = null, string? listUrlBase = null)
        {
            var webUrl = $"{Root}/sites/{site}";
            Hits.Add(new DiscoveredLibrary("aaaaaaaa-0000-0000-0000-000000000001", $"bbbbbbbb-0000-0000-0000-{site.GetHashCode() & 0xFFFF:x12}", listId, webUrl, webUrl, site, title));
            Lists[$"{listUrlBase ?? webUrl}|{listId}"] = metadata ?? new ListMetadata(title, false, 101, false, false, null, false, false, 5);
        }
    }

    static Task<DiscoveryResult> Run(FakeApi api, DiscoveryState? previous = null, TimeSpan later = default) =>
        new DiscoveryService(api, new DiscoveryOptions { Clock = () => DateTimeOffset.UtcNow + later }).RunAsync(previous);

    static readonly TimeSpan NextDay = TimeSpan.FromHours(49);

    static string Id(int n) => $"00000000-0000-0000-0000-{n:x12}";

    [Fact]
    public async Task Shows_libraries_and_applies_site_and_library_rules()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Finance", Id(2), "Site Assets", new ListMetadata("Site Assets", false, 101, false, false, null, false, false, 5, "SiteAssets"));
        api.Add("Finance", Id(3), "Contracts", new ListMetadata("Contracts", false, 101, false, false, null, true, false, 5));
        api.Add("AppCatalog", Id(4), "Apps");
        api.Add("Locked", Id(5), "Documents");
        api.Sites[$"{Root}/sites/Locked"] = new SiteMetadata(true, false);

        var result = await Run(api);

        Assert.Equal(Root, result.TenantRoot);
        Assert.Equal(["Finance/Contracts:ro", "Finance/Documents:rw", "Locked/Documents:ro"],
            result.Libraries.Select(l => $"{l.SiteTitle}/{l.LibraryTitle}:{(l.ReadOnly ? "ro" : "rw")}"));
        Assert.Contains(result.Skipped, s => s.Title == "Site Assets" && s.Reason == "System or catalog library");
        Assert.Contains(result.Skipped, s => s.Title == "Apps" && s.Reason == "Site excluded by pattern");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Static_exclusions_are_not_looked_up_again()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Finance", Id(2), "Stijlbibliotheek", new ListMetadata("Stijlbibliotheek", false, 101, false, false, null, false, false, 5, "Style Library"));

        var first = await Run(api);
        api.ListCalls = 0;
        var second = await Run(api, first.State, NextDay);

        Assert.Equal(1, api.ListCalls);
        Assert.Single(second.State.StaticExclusions);
        Assert.Single(second.Libraries);
    }

    [Fact]
    public async Task Known_libraries_reuse_their_metadata_for_two_days()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        var first = await Run(api) is var r ? r with { State = r.State with { Libraries = [r.Libraries[0] with { DriveId = "b!finance" }] } } : null!;
        api.Add("Sales", Id(2), "Documents");
        api.ListCalls = 0;

        var sameDay = await Run(api, first.State);
        Assert.Equal(1, api.ListCalls);
        Assert.Equal(2, sameDay.Libraries.Count);
        Assert.Equal(first.State.MetadataAt, sameDay.State.MetadataAt);

        api.ListCalls = 0;
        var nextDay = await Run(api, sameDay.State, NextDay);
        Assert.Equal(2, api.ListCalls);
        Assert.True(nextDay.State.MetadataAt > first.State.MetadataAt);
        Assert.Equal("b!finance", nextDay.Libraries.Single(l => l.SiteTitle == "Finance").DriveId);
        Assert.Equal("b!me", nextDay.State.OneDrive?.DriveId);
    }

    [Fact]
    public async Task Each_library_is_looked_up_again_only_when_its_own_metadata_is_two_days_old()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        var first = await Run(api);
        api.Add("Sales", Id(2), "Documents");

        api.ListCalls = 0;
        var dayTwo = await Run(api, first.State, TimeSpan.FromHours(30));
        Assert.Equal(1, api.ListCalls); // Sales is new; Finance is 30 h old

        api.ListCalls = 0;
        var dayThree = await Run(api, dayTwo.State, TimeSpan.FromHours(50));
        Assert.Equal(1, api.ListCalls); // Finance is 50 h old; Sales 20 h
        Assert.Equal(["Finance", "Sales"], dayThree.Libraries.Select(l => l.SiteTitle).Order());

        api.ListCalls = 0;
        await new DiscoveryService(api, new DiscoveryOptions { MetadataMaxAge = TimeSpan.Zero }).RunAsync(dayThree.State);
        Assert.Equal(2, api.ListCalls); // a refresh reads everything
    }

    [Fact]
    public async Task State_of_earlier_versions_without_check_times_uses_its_metadata_time()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        var first = await Run(api);
        var old = first.State with { Libraries = [.. first.State.Libraries.Select(l => l with { CheckedAt = null })] };

        api.ListCalls = 0;
        await Run(api, old, TimeSpan.FromHours(30));
        Assert.Equal(0, api.ListCalls);
        await Run(api, old, TimeSpan.FromHours(50));
        Assert.Equal(1, api.ListCalls);
    }

    static PinnedLocation Pinned(string web, params (string Id, string Title, string? InternalName)[] lists) =>
        new(web, Id(90), Id(91), "Pinned site", new SiteMetadata(false, false),
            lists.Select(l => new PinnedLibrary(l.Id, new ListMetadata(l.Title, false, 101, false, false, null, false, false, 3, l.InternalName))).ToList());

    [Fact]
    public async Task Pinned_locations_show_where_search_does_not_reach()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Pins["https://contoso.sharepoint.com/portals/hub"] = Pinned("https://contoso.sharepoint.com/portals/hub", (Id(20), "Handbooks", null), (Id(21), "Site Assets", "SiteAssets"));
        var options = new DiscoveryOptions { PinnedLocations = ["https://contoso.sharepoint.com/portals/hub", " "] };

        var first = await new DiscoveryService(api, options).RunAsync(null);
        api.Resolves = 0;
        var sameDay = await new DiscoveryService(api, options).RunAsync(first.State);
        var nextDay = await new DiscoveryService(api, options with { Clock = () => DateTimeOffset.UtcNow + NextDay }).RunAsync(sameDay.State);

        Assert.Equal(["Documents", "Handbooks"], first.Libraries.Select(l => l.LibraryTitle).Order());
        Assert.Equal("Pinned site", first.Libraries.Single(l => l.LibraryTitle == "Handbooks").SiteTitle);
        Assert.Equal(2, sameDay.Libraries.Count);
        Assert.Equal(1, api.Resolves);
        Assert.Equal(2, nextDay.Libraries.Count);
    }

    [Fact]
    public async Task A_pinned_location_that_fails_keeps_its_libraries()
    {
        var api = new FakeApi();
        api.Pins["https://contoso.sharepoint.com/sites/Archive"] = Pinned("https://contoso.sharepoint.com/sites/Archive", (Id(30), "Old", null));
        var options = new DiscoveryOptions { PinnedLocations = ["https://contoso.sharepoint.com/sites/Archive"] };
        var first = await new DiscoveryService(api, options).RunAsync(null);

        api.FailPins = true;
        var later = await new DiscoveryService(api, options with { Clock = () => DateTimeOffset.UtcNow + NextDay }).RunAsync(first.State);

        Assert.Equal(["Old"], later.Libraries.Select(l => l.LibraryTitle));
        Assert.Contains(later.Errors, e => e.Reason.StartsWith("Pinned location"));
    }

    [Fact]
    public async Task Static_exclusions_from_older_rules_are_dropped()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        var first = await Run(api);
        var stale = first.State with
        {
            StaticExclusions = new Dictionary<string, string> { [first.Libraries[0].Key] = "old rule" },
            RulesVersion = LibraryRules.Version - 1,
        };

        var second = await Run(api, stale);

        Assert.Single(second.Libraries);
    }

    [Fact]
    public async Task List_is_retried_on_the_site_collection_url()
    {
        var api = new FakeApi();
        var webUrl = $"{Root}/sites/Finance/Sub";
        api.Hits.Add(new DiscoveredLibrary(Id(10), Id(11), Id(12), webUrl, $"{Root}/sites/Finance", "Sub", "Documents"));
        api.Lists[$"{Root}/sites/Finance|{Id(12)}"] = new ListMetadata("Documents", false, 101, false, false, null, false, false, 1);

        var result = await Run(api);

        Assert.Single(result.Libraries);
        Assert.Equal(2, api.ListCalls);
    }

    [Fact]
    public async Task Failed_search_hides_nothing()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Sales", Id(2), "Documents");
        var first = await Run(api);

        api.FailSearch = true;
        var second = await Run(api, first.State);

        Assert.NotNull(second.SearchError);
        Assert.Equal(2, second.Libraries.Count);
        Assert.NotNull(second.HidingSkippedReason);
    }

    [Fact]
    public async Task Small_shrink_hides_but_large_shrink_hides_nothing()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 10; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        var first = await Run(api);

        api.Hits.RemoveAt(0);
        var small = await Run(api, first.State);
        Assert.Equal(9, small.Libraries.Count);
        Assert.Null(small.HidingSkippedReason);

        api.Hits.RemoveRange(0, 5);
        var large = await Run(api, small.State);
        Assert.Equal(9, large.Libraries.Count);
        Assert.Contains("shrank from 9 to 4", large.HidingSkippedReason);
    }

    [Fact]
    public async Task Library_that_fails_keeps_its_previous_entry()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Sales", Id(2), "Documents");
        var first = await Run(api);

        api.FailingSites.Add($"{Root}/sites/Sales");
        var second = await Run(api, first.State, NextDay);

        Assert.Equal(2, second.Libraries.Count);
        Assert.Single(second.Errors);
        Assert.Null(second.HidingSkippedReason);
    }

    [Fact]
    public async Task Previous_state_of_another_tenant_is_ignored()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        var first = await Run(api);

        api.OneDriveUrl = "https://fabrikam-my.sharepoint.com/personal/x/Documents";
        api.Hits.Clear();
        var second = await Run(api, first.State);

        Assert.Equal("https://fabrikam.sharepoint.com", second.TenantRoot);
        Assert.Empty(second.Libraries);
        Assert.Null(second.HidingSkippedReason);
    }
}
