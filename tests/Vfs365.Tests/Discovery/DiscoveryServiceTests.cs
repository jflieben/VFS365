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
        public int FailFromPage = int.MaxValue;
        public bool NoDocIds;
        public Func<int, long> DocIdOf = i => i + 1L;
        public Func<int, string?> GeoOf = _ => null;
        public bool SortsByDocId = true;
        public bool FiltersByDocId = true;
        /// <summary>"web|site", "web|lists" and "base|listId" calls that time out.</summary>
        public HashSet<string> TimingOut = new(StringComparer.OrdinalIgnoreCase);
        public List<(long After, int StartRow)> Searches = [];
        public HashSet<string> FailingSites = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unlistable = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, SiteMetadata> Sites = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ListMetadata> Lists = new(StringComparer.OrdinalIgnoreCase);
        public int ListCalls;
        public int WebListCalls;
        public int DriveCalls;

        public Task<MyDrive> GetMyDriveAsync(CancellationToken ct)
        {
            DriveCalls++;
            return Task.FromResult(new MyDrive("b!me", OneDriveUrl));
        }

        /// <summary>Hit i has DocId DocIdOf(i) (i + 1); in DocId order unless SortsByDocId is off (then in hit order, like rank).</summary>
        public Task<SearchPage> SearchDocumentLibrariesAsync(string tenantRoot, long afterDocId, int startRow, int rowLimit, CancellationToken ct)
        {
            if (FailSearch || Searches.Count + 1 >= FailFromPage)
            {
                throw new HttpRequestException("search down");
            }
            Searches.Add((afterDocId, startRow));
            var matches = Hits.Select((hit, i) => (Hit: hit, DocId: DocIdOf(i), Geo: GeoOf(i))).Where(r => !FiltersByDocId || r.DocId > afterDocId);
            var rows = (SortsByDocId ? matches.OrderBy(r => r.DocId) : matches).Skip(startRow).Take(rowLimit).ToList();
            return Task.FromResult(new SearchPage(rows.Select(r => r.Hit).ToList(), rows.Count, NoDocIds || rows.Count == 0 ? null : rows[^1].DocId,
                NoDocIds ? null : rows.Select(r => (long?)r.DocId).ToList(), rows.Select(r => r.Geo).OfType<string>().Distinct().ToList()));
        }

        static Exception Timeout() => new TaskCanceledException("no answer within 100 s (timed out)", new TimeoutException());

        public Task<SiteMetadata> GetSiteAsync(string webUrl, CancellationToken ct) =>
            TimingOut.Contains($"{webUrl}|site") ? throw Timeout()
            : FailingSites.Contains(webUrl) ? throw new HttpRequestException("site down") : Task.FromResult(Sites.GetValueOrDefault(webUrl, new SiteMetadata(false, false)));

        public Task<IReadOnlyList<PinnedLibrary>?> GetWebLibrariesAsync(string webUrl, CancellationToken ct)
        {
            Interlocked.Increment(ref WebListCalls);
            if (TimingOut.Contains($"{webUrl}|lists"))
            {
                throw Timeout();
            }
            return Task.FromResult<IReadOnlyList<PinnedLibrary>?>(Unlistable.Contains(webUrl) ? null
                : Lists.Where(l => l.Key.StartsWith(webUrl + "|", StringComparison.OrdinalIgnoreCase)).Select(l => new PinnedLibrary(l.Key[(webUrl.Length + 1)..], l.Value)).ToList());
        }

        public Task<ListMetadata?> GetListAsync(string baseUrl, string listId, CancellationToken ct)
        {
            Interlocked.Increment(ref ListCalls);
            if (TimingOut.Contains($"{baseUrl}|{listId}"))
            {
                throw Timeout();
            }
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

        public void Add(string site, string listId, string title, ListMetadata? metadata = null, string? listUrlBase = null, string? template = null)
        {
            var webUrl = $"{Root}/sites/{site}";
            Hits.Add(new DiscoveredLibrary("aaaaaaaa-0000-0000-0000-000000000001", $"bbbbbbbb-0000-0000-0000-{site.GetHashCode() & 0xFFFF:x12}", listId, webUrl, webUrl, site, title, template));
            Lists[$"{listUrlBase ?? webUrl}|{listId}"] = metadata ?? new ListMetadata(title, false, 101, false, false, null, false, false, 5);
        }
    }

    static readonly DateTimeOffset Start = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Without the first-read jitter, so check times are exact.</summary>
    static Task<DiscoveryResult> Run(FakeApi api, DiscoveryState? previous = null, TimeSpan later = default) =>
        new DiscoveryService(api, new DiscoveryOptions { Clock = () => Start + later, FirstReadJitter = TimeSpan.Zero }).RunAsync(previous);

    /// <summary>Past the default metadata age (7 days).</summary>
    static readonly TimeSpan NextWeek = TimeSpan.FromDays(7) + TimeSpan.FromHours(1);

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

    /// <summary>Teams-connected, communication, channel and unreported sites.</summary>
    static FakeApi TemplateSites()
    {
        var api = new FakeApi();
        api.Add("Sales", Id(1), "Documents", template: "GROUP");
        api.Add("Intranet", Id(2), "Documents", template: "SITEPAGEPUBLISHING");
        api.Add("Sales-Private", Id(3), "Documents", template: "TEAMCHANNEL");
        api.Add("Old", Id(4), "Documents");
        return api;
    }

    static async Task<string[]> Shown(FakeApi api, DiscoveryOptions options) =>
        (await new DiscoveryService(api, options).RunAsync(null)).Libraries.Select(l => l.SiteTitle).Order().ToArray();

    [Fact]
    public async Task Site_templates_can_be_excluded_or_be_the_only_ones_shown()
    {
        var excluded = await new DiscoveryService(TemplateSites(), new DiscoveryOptions { ExcludedSiteTemplates = ["teamchannel"] }).RunAsync(null);
        Assert.Equal(["Intranet", "Old", "Sales"], excluded.Libraries.Select(l => l.SiteTitle).Order());
        Assert.Contains(excluded.Skipped, s => s.WebUrl.EndsWith("Sales-Private") && s.Reason == "Site template TEAMCHANNEL excluded");
        Assert.Equal("GROUP", excluded.Libraries.Single(l => l.SiteTitle == "Sales").SiteTemplate);

        // A configuration number is ignored (search reports none); a template search didn't report is never held against a site
        Assert.Equal(["Old", "Sales", "Sales-Private"], await Shown(TemplateSites(), new DiscoveryOptions { IncludedSiteTemplates = ["GROUP#0", "TEAM*"] }));
        Assert.Equal(["Old", "Sales"], await Shown(TemplateSites(), new DiscoveryOptions { IncludedSiteTemplates = ["GROUP", "TEAMCHANNEL"], ExcludedSiteTemplates = ["TEAMCHANNEL"] }));
    }

    [Fact]
    public async Task Libraries_hidden_by_policy_go_even_when_most_of_them_do()
    {
        var api = TemplateSites();
        for (var i = 0; i < 6; i++)
        {
            api.Add("Sales", Id(10 + i), $"Library {i}", template: "GROUP");
        }
        var before = await Run(api);
        Assert.Equal(10, before.Libraries.Count);

        var after = await new DiscoveryService(api, new DiscoveryOptions { ExcludedSiteTemplates = ["GROUP"] }).RunAsync(before.State);

        Assert.Null(after.HidingSkippedReason);
        Assert.Equal(["Intranet", "Old", "Sales-Private"], after.Libraries.Select(l => l.SiteTitle).Order());
    }

    [Fact]
    public async Task Static_exclusions_are_not_looked_up_again()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Finance", Id(2), "Stijlbibliotheek", new ListMetadata("Stijlbibliotheek", false, 101, false, false, null, false, false, 5, "Style Library"));

        var first = await Run(api);
        api.ListCalls = 0;
        var second = await Run(api, first.State, NextWeek);

        Assert.Equal(1, api.ListCalls);
        Assert.Single(second.State.StaticExclusions);
        Assert.Single(second.Libraries);
    }

    [Fact]
    public async Task Known_libraries_reuse_their_metadata_for_a_week()
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
        var nextWeek = await Run(api, sameDay.State, NextWeek);
        Assert.Equal(2, api.ListCalls);
        Assert.True(nextWeek.State.MetadataAt > first.State.MetadataAt);
        Assert.Equal("b!finance", nextWeek.Libraries.Single(l => l.SiteTitle == "Finance").DriveId);
        Assert.Equal("b!me", nextWeek.State.OneDrive?.DriveId);
    }

    [Fact]
    public async Task Each_library_is_looked_up_again_only_when_its_own_metadata_is_a_week_old()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        var first = await Run(api);
        api.Add("Sales", Id(2), "Documents");

        api.ListCalls = 0;
        var dayFour = await Run(api, first.State, TimeSpan.FromDays(4));
        Assert.Equal(1, api.ListCalls); // Sales is new; Finance is 4 days old

        api.ListCalls = 0;
        var dayEight = await Run(api, dayFour.State, TimeSpan.FromDays(8));
        Assert.Equal(1, api.ListCalls); // Finance is 8 days old; Sales 4
        Assert.Equal(["Finance", "Sales"], dayEight.Libraries.Select(l => l.SiteTitle).Order());

        api.ListCalls = 0;
        await new DiscoveryService(api, new DiscoveryOptions { MetadataMaxAge = TimeSpan.Zero }).RunAsync(dayEight.State);
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
        await Run(api, old, TimeSpan.FromDays(4));
        Assert.Equal(0, api.ListCalls);
        await Run(api, old, TimeSpan.FromDays(8));
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
        var nextWeek = await new DiscoveryService(api, options with { Clock = () => DateTimeOffset.UtcNow + NextWeek }).RunAsync(sameDay.State);

        Assert.Equal(["Documents", "Handbooks"], first.Libraries.Select(l => l.LibraryTitle).Order());
        Assert.Equal("Pinned site", first.Libraries.Single(l => l.LibraryTitle == "Handbooks").SiteTitle);
        Assert.Equal(2, sameDay.Libraries.Count);
        Assert.Equal(1, api.Resolves);
        Assert.Equal(2, nextWeek.Libraries.Count);
    }

    [Fact]
    public async Task A_pinned_location_that_fails_keeps_its_libraries()
    {
        var api = new FakeApi();
        api.Pins["https://contoso.sharepoint.com/sites/Archive"] = Pinned("https://contoso.sharepoint.com/sites/Archive", (Id(30), "Old", null));
        var options = new DiscoveryOptions { PinnedLocations = ["https://contoso.sharepoint.com/sites/Archive"] };
        var first = await new DiscoveryService(api, options).RunAsync(null);

        api.FailPins = true;
        var later = await new DiscoveryService(api, options with { Clock = () => DateTimeOffset.UtcNow + NextWeek }).RunAsync(first.State);

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
    public async Task A_search_that_fails_on_a_later_page_is_a_failed_run_that_keeps_the_last_libraries()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 1200; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        var first = await Run(api);
        Assert.Null(first.SearchError);
        Assert.Equal(Start, first.State.RunAt);

        api.Searches.Clear();
        api.FailFromPage = 2;
        var second = await Run(api, first.State, TimeSpan.FromHours(1));

        Assert.StartsWith("Search failed after 500 rows", second.SearchError);
        Assert.Equal(1200, second.Libraries.Count);
        Assert.Empty(second.State.Misses!); // an incomplete search counts no misses
        Assert.Equal(Start, second.State.RunAt); // not a recent run for the next start
    }

    [Fact]
    public async Task A_first_run_whose_search_fails_shows_what_it_found_and_is_no_recent_run()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 600; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        api.FailFromPage = 2;
        var partly = await Run(api);
        Assert.NotNull(partly.SearchError);
        Assert.Equal(500, partly.Libraries.Count);
        Assert.Equal(DateTimeOffset.MinValue, partly.State.RunAt);

        api.FailSearch = true;
        var nothing = await Run(api);
        Assert.NotNull(nothing.SearchError);
        Assert.Empty(nothing.Libraries);
        Assert.Equal(DateTimeOffset.MinValue, nothing.State.RunAt);
    }

    [Fact]
    public async Task A_site_that_times_out_is_that_sites_error_and_the_run_goes_on()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Sales", Id(2), "Documents");
        api.Add("Legal", Id(3), "Documents");
        api.Add("Legal", Id(4), "Contracts");
        api.Add("Board", Id(5), "Documents");
        var first = await Run(api);

        api.TimingOut.Add($"{Root}/sites/Sales|site");
        api.TimingOut.Add($"{Root}/sites/Legal|lists"); // falls back to one call per library
        api.TimingOut.Add($"{Root}/sites/Board|{Id(5)}");
        var second = await Run(api, first.State, NextWeek);

        Assert.Equal(5, second.Libraries.Count); // the failed ones keep their last entries
        Assert.Equal(["Board", "Sales"], second.Errors.Select(e => e.WebUrl[(e.WebUrl.LastIndexOf('/') + 1)..]).Order());
        Assert.All(second.Errors, e => Assert.Contains("timed out", e.Reason));
        Assert.Null(second.SearchError);
    }

    [Fact]
    public async Task Cancelling_a_run_still_cancels_it()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DiscoveryService(api, new DiscoveryOptions()).RunAsync(null, cancel.Token));
    }

    [Fact]
    public async Task Small_shrink_hides_but_large_shrink_waits()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 10; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        var first = await Run(api);

        api.Hits.RemoveAt(0);
        var small = await Run(api, await Run(api, first.State) is var once ? once.State : null);
        Assert.Equal(9, small.Libraries.Count);
        Assert.Null(small.HidingSkippedReason);

        api.Hits.RemoveRange(0, 5);
        var large = await Run(api, await Run(api, small.State) is var largeOnce ? largeOnce.State : null);
        Assert.Equal(9, large.Libraries.Count);
        Assert.Contains("shrank from 9 to 4", large.HidingSkippedReason);
        Assert.All(large.State.Misses!.Values, count => Assert.Equal(2, count)); // misses count while the shrink waits
    }

    [Fact]
    public async Task A_large_shrink_is_accepted_after_three_runs()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 10; i++)
        {
            api.Add($"Team{i}", Id(i), "Documents");
        }
        var before = await Run(api);
        api.Hits.RemoveRange(0, 6); // the user left most of their Teams

        var one = await Run(api, before.State);
        var two = await Run(api, one.State);
        var three = await Run(api, two.State);

        Assert.Equal([10, 10, 4], new[] { one, two, three }.Select(r => r.Libraries.Count));
        Assert.Contains("shrank from 10 to 4", one.HidingSkippedReason);
        Assert.Contains("shrank from 10 to 4", two.HidingSkippedReason);
        Assert.Null(three.HidingSkippedReason);
        Assert.Empty(three.State.Misses!);
    }

    [Fact]
    public async Task A_library_missing_from_one_run_stays_and_goes_after_the_second()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 5; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        var first = await Run(api);
        var gone = api.Hits[0];

        api.Hits.RemoveAt(0);
        var once = await Run(api, first.State);
        Assert.Equal(5, once.Libraries.Count);
        Assert.Equal(1, once.State.Misses![gone.Key]);

        api.Hits.Insert(0, gone); // back: the count starts over
        var back = await Run(api, once.State);
        Assert.Empty(back.State.Misses!);

        api.Hits.RemoveAt(0);
        var twice = await Run(api, await Run(api, back.State) is var again ? again.State : null);
        Assert.Equal(4, twice.Libraries.Count);
        Assert.DoesNotContain(twice.Libraries, l => l.Key == gone.Key);
    }

    [Fact]
    public async Task A_library_the_rules_now_exclude_goes_at_once()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Sales", Id(2), "Documents");
        var first = await Run(api);

        api.Lists[$"{Root}/sites/Sales|{Id(2)}"] = new ListMetadata("Documents", false, 101, false, false, null, false, true, 5);
        var second = await Run(api, first.State, NextWeek);

        Assert.Equal(["Finance"], second.Libraries.Select(l => l.SiteTitle));
    }

    [Fact]
    public async Task A_site_answers_for_its_libraries_in_one_call()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Finance", Id(2), "Contracts");
        api.Add("Finance", Id(3), "Site Assets", new ListMetadata("Site Assets", false, 101, false, false, null, false, false, 5, "SiteAssets"));

        var result = await Run(api);

        Assert.Equal(1, api.WebListCalls);
        Assert.Equal(0, api.ListCalls);
        Assert.Equal(["Contracts", "Documents"], result.Libraries.Select(l => l.LibraryTitle));
    }

    [Fact]
    public async Task A_site_the_user_cannot_list_is_read_per_library()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Finance", Id(2), "Contracts");
        api.Unlistable.Add($"{Root}/sites/Finance");

        var result = await Run(api);

        Assert.Equal(2, api.ListCalls);
        Assert.Equal(2, result.Libraries.Count);
    }

    [Fact]
    public async Task A_library_missing_from_the_site_list_is_read_by_id_also_on_the_site_collection()
    {
        var api = new FakeApi();
        var webUrl = $"{Root}/sites/Finance/Sub";
        api.Hits.Add(new DiscoveredLibrary(Id(10), Id(11), Id(12), webUrl, $"{Root}/sites/Finance", "Sub", "Documents"));
        api.Hits.Add(new DiscoveredLibrary(Id(10), Id(11), Id(13), webUrl, $"{Root}/sites/Finance", "Sub", "Archive"));
        api.Lists[$"{webUrl}|{Id(12)}"] = new ListMetadata("Documents", false, 101, false, false, null, false, false, 1);
        api.Lists[$"{Root}/sites/Finance|{Id(13)}"] = new ListMetadata("Archive", false, 101, false, false, null, false, false, 1);

        var result = await Run(api);

        Assert.Equal(1, api.WebListCalls);
        Assert.Equal(2, api.ListCalls); // Archive: on the web, then on the site collection
        Assert.Equal(2, result.Libraries.Count);
    }

    [Fact]
    public async Task First_reads_spread_their_check_times_the_same_way_every_time()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 20; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        var options = new DiscoveryOptions { Clock = () => Start };

        var first = await new DiscoveryService(api, options).RunAsync(null);
        var again = await new DiscoveryService(api, options).RunAsync(null);

        var offsets = first.Libraries.Select(l => Start - l.CheckedAt!.Value).ToList();
        Assert.All(offsets, o => Assert.InRange(o, TimeSpan.Zero, TimeSpan.FromHours(24)));
        Assert.True(offsets.Distinct().Count() > 10);
        Assert.Equal(first.Libraries.Select(l => l.CheckedAt), again.Libraries.Select(l => l.CheckedAt));
    }

    [Fact]
    public async Task Expired_sites_are_read_again_a_share_per_run_oldest_first()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 30; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        var options = new DiscoveryOptions { Clock = () => Start, FirstReadJitter = TimeSpan.Zero, MinSiteRechecks = 5 };
        var first = await new DiscoveryService(api, options).RunAsync(null);
        // Sites 1 to 10 a day older than the rest
        var aged = first.State with { Libraries = [.. first.State.Libraries.Select(l => int.Parse(l.SiteTitle[4..]) <= 10 ? l with { CheckedAt = Start - TimeSpan.FromDays(1) } : l)] };

        api.ListCalls = 0;
        var second = await new DiscoveryService(api, options with { Clock = () => Start + NextWeek }).RunAsync(aged);
        Assert.Equal(15, api.ListCalls);
        Assert.Equal(30, second.Libraries.Count);
        Assert.All(second.Libraries.Where(l => int.Parse(l.SiteTitle[4..]) <= 10), l => Assert.Equal(Start + NextWeek, l.CheckedAt));

        api.ListCalls = 0;
        await new DiscoveryService(api, options with { Clock = () => Start + NextWeek + TimeSpan.FromHours(1) }).RunAsync(second.State);
        Assert.Equal(15, api.ListCalls);

        api.ListCalls = 0;
        await new DiscoveryService(api, options with { MetadataMaxAge = TimeSpan.Zero }).RunAsync(second.State);
        Assert.Equal(30, api.ListCalls); // a refresh isn't held back
    }

    [Fact]
    public async Task Search_pages_after_the_last_doc_id_or_by_row_without_one()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 1200; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }

        var byDocId = await Run(api);
        Assert.Equal([(0L, 0), (500L, 0), (1000L, 0)], api.Searches);
        Assert.Equal(1200, byDocId.SearchHits);

        api.Searches.Clear();
        api.NoDocIds = true;
        var byRow = await Run(api);
        Assert.Equal([(0L, 0), (0L, 500), (0L, 1000)], api.Searches);
        Assert.Equal(1200, byRow.SearchHits);
        Assert.Null(byRow.SearchNote);
    }

    static FakeApi ManySites(int count = 1200)
    {
        var api = new FakeApi();
        for (var i = 1; i <= count; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        return api;
    }

    [Fact]
    public async Task Search_pages_by_row_when_the_doc_ids_come_out_of_order()
    {
        var api = ManySites();
        api.DocIdOf = i => i * 7919L % 100_000 + 1; // unique, not rising
        api.SortsByDocId = false; // the sort isn't honoured: rank order

        var result = await Run(api);

        Assert.Equal([(0L, 0), (0L, 500), (0L, 1000)], api.Searches);
        Assert.Equal(1200, result.Libraries.Count);
        Assert.Null(result.SearchError);
        Assert.Contains("row number", result.SearchNote);
    }

    [Fact]
    public async Task Search_starts_over_by_row_when_a_page_repeats_doc_ids()
    {
        var api = ManySites();
        api.FiltersByDocId = false; // IndexDocId> isn't honoured: page 2 is page 1 again

        var result = await Run(api);

        Assert.Equal([(0L, 0), (500L, 0), (0L, 0), (0L, 500), (0L, 1000)], api.Searches);
        Assert.Equal(1200, result.SearchHits);
        Assert.Equal(1200, result.Libraries.Count);
        Assert.Contains("DocId 1 twice", result.SearchNote);
    }

    [Fact]
    public async Task Search_pages_by_row_when_results_come_from_several_geos()
    {
        var api = ManySites();
        api.GeoOf = i => i % 2 == 0 ? "EUR" : "NAM";

        var result = await Run(api);

        Assert.Equal([(0L, 0), (0L, 500), (0L, 1000)], api.Searches);
        Assert.Equal(1200, result.Libraries.Count);
        Assert.Contains("several geos", result.SearchNote);
    }

    [Fact]
    public async Task A_hidden_library_is_checked_again_with_the_metadata()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Finance", Id(2), "Archive", new ListMetadata("Archive", true, 101, false, false, null, false, false, 5));
        var first = await Run(api);
        Assert.Single(first.Libraries);
        Assert.Empty(first.State.StaticExclusions);
        Assert.Equal(LibraryRules.HiddenReason, first.State.Exclusions![api.Hits[1].Key].Reason);

        api.WebListCalls = api.ListCalls = 0;
        var sameDay = await Run(api, first.State);
        Assert.Equal(0, api.WebListCalls + api.ListCalls);
        Assert.Contains(sameDay.Skipped, s => s.Reason == LibraryRules.HiddenReason);

        api.Lists[$"{Root}/sites/Finance|{Id(2)}"] = new ListMetadata("Archive", false, 101, false, false, null, false, false, 5);
        var later = await Run(api, sameDay.State, NextWeek);
        Assert.Equal(2, later.Libraries.Count);

        // Cached as static by earlier versions: read again
        var old = first.State with { StaticExclusions = new Dictionary<string, string> { [api.Hits[1].Key] = LibraryRules.HiddenReason }, Exclusions = null };
        Assert.Equal(2, (await Run(api, old)).Libraries.Count);
    }

    [Fact]
    public async Task A_pinned_url_with_nothing_there_is_probed_again_only_after_the_metadata_age()
    {
        var api = new FakeApi();
        var options = new DiscoveryOptions { PinnedLocations = ["https://contoso.sharepoint.com/sites/Gone"], Clock = () => Start };

        var first = await new DiscoveryService(api, options).RunAsync(null);
        var sameDay = await new DiscoveryService(api, options).RunAsync(first.State);
        Assert.Equal(1, api.Resolves);
        Assert.Contains(first.Errors, e => e.Reason == "Pinned location: nothing found there");
        Assert.Contains(sameDay.Skipped, s => s.Reason == "Pinned location: nothing found there");

        await new DiscoveryService(api, options with { Clock = () => Start + NextWeek }).RunAsync(sameDay.State);
        Assert.Equal(2, api.Resolves);
    }

    [Fact]
    public async Task A_known_onedrive_is_not_read_again()
    {
        var api = new FakeApi();
        var result = await new DiscoveryService(api, new DiscoveryOptions()).RunAsync(null, default, new MyDrive("b!known", api.OneDriveUrl));

        Assert.Equal(0, api.DriveCalls);
        Assert.Equal("b!known", result.State.OneDrive?.DriveId);
    }

    [Fact]
    public async Task A_user_moved_to_another_geo_keeps_the_state_of_the_same_tenant()
    {
        var api = new FakeApi();
        for (var i = 1; i <= 5; i++)
        {
            api.Add($"Site{i}", Id(i), "Documents");
        }
        var service = new DiscoveryService(api, new DiscoveryOptions { Clock = () => Start });
        var first = await service.RunAsync(null, tenantId: "tenant-1");
        api.OneDriveUrl = "https://contosoeur-my.sharepoint.com/personal/jos_contoso_com/Documents";
        api.Hits.Clear(); // search in the new geo doesn't answer yet

        api.ListCalls = 0;
        var moved = await service.RunAsync(first.State, tenantId: "tenant-1");
        Assert.Equal("https://contosoeur.sharepoint.com", moved.TenantRoot);
        Assert.Equal(5, moved.Libraries.Count); // the safety check still holds
        Assert.Equal("tenant-1", moved.State.TenantId);

        var withoutId = await service.RunAsync(first.State);
        Assert.Empty(withoutId.Libraries); // by host: another tenant
    }

    [Fact]
    public async Task Library_that_fails_keeps_its_previous_entry()
    {
        var api = new FakeApi();
        api.Add("Finance", Id(1), "Documents");
        api.Add("Sales", Id(2), "Documents");
        var first = await Run(api);

        api.FailingSites.Add($"{Root}/sites/Sales");
        var second = await Run(api, first.State, NextWeek);

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
