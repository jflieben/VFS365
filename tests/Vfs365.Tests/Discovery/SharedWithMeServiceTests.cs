using System.Text.Json;
using Vfs365.Core.Discovery;
using Vfs365.Graph;

namespace Vfs365.Tests.Discovery;

public sealed class SharedWithMeServiceTests
{
    sealed class FakeApi : ISharedWithMeApi
    {
        public List<SharedItem> Hits { get; set; } = [];
        public Dictionary<string, SharedLocation?> Locations { get; } = [];
        public Exception? SearchFails { get; set; }
        public Exception? LookupFails { get; set; }
        public bool? CanEdit { get; set; } = true;
        public int Searches, Lookups, RightsChecks;

        public Task<SharedSearchPage> SearchAsync(string user, int from, int size, CancellationToken ct)
        {
            Searches++;
            if (SearchFails is { } e)
            {
                throw e;
            }
            var page = Hits.Skip(from).Take(size).ToList();
            return Task.FromResult(new SharedSearchPage(page, from + page.Count < Hits.Count, page.Count));
        }

        public Task<SharedLocation?> GetLocationAsync(string driveId, string itemId, CancellationToken ct)
        {
            Interlocked.Increment(ref Lookups);
            if (LookupFails is { } e)
            {
                throw e;
            }
            return Task.FromResult(Locations.TryGetValue($"{driveId}|{itemId}", out var known) ? known
                : new SharedLocation($"Docs/{itemId}", itemId, "parent", itemId.StartsWith("folder", StringComparison.Ordinal), "https://x-my.sharepoint.com/personal/a", "l", "1"));
        }

        public Task<bool?> CanEditAsync(SharedLocation location, CancellationToken ct)
        {
            Interlocked.Increment(ref RightsChecks);
            return Task.FromResult(CanEdit);
        }
    }

    readonly FakeApi api = new();
    DateTimeOffset now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    SharedWithMeService Service(int pageSize = 200) => new(api, new SharedOptions { PageSize = pageSize, Clock = () => now });

    static SharedItem Hit(string id, string drive = "b!alice", string? parent = "parent") => new(drive, id, id, id.StartsWith("folder", StringComparison.Ordinal), ParentId: parent, OwnerName: "Alice");

    [Fact]
    public async Task Own_items_and_duplicates_are_left_out_and_each_item_is_placed_and_checked()
    {
        api.Hits = [Hit("a"), Hit("folder1"), Hit("a"), Hit("mine", drive: "b!me")];
        api.CanEdit = false;

        var result = await Service().RunAsync("u@x.com", "b!me", null, recheck: false, default);

        Assert.Null(result.SearchError);
        Assert.Equal(["b!alice|a", "b!alice|folder1"], result.State.Items.Select(i => i.Key));
        Assert.Equal("Docs/a", result.State.Items[0].Path);
        Assert.All(result.State.Items, i => Assert.True(i.ReadOnly));
        Assert.True(result.State.Items[1].IsFolder);
        Assert.Equal(now, result.State.RunAt);
        Assert.Equal((1, 2, 2), (api.Searches, api.Lookups, api.RightsChecks));
    }

    [Fact]
    public async Task A_routine_run_is_one_search_call_and_moves_renames_and_old_checks_are_read_again()
    {
        api.Hits = [Hit("a"), Hit("b"), Hit("c")];
        var first = await Service().RunAsync("u@x.com", "b!me", null, false, default);
        (api.Lookups, api.RightsChecks) = (0, 0);

        now += TimeSpan.FromHours(1);
        var second = await Service().RunAsync("u@x.com", "b!me", first.State, false, default);
        Assert.Equal((2, 0, 0), (api.Searches, api.Lookups, api.RightsChecks));
        Assert.Equal(first.State.Items.Select(i => i.Path), second.State.Items.Select(i => i.Path));

        // b moved (another parent), c renamed: those two are read again
        api.Hits = [Hit("a"), Hit("b", parent: "elsewhere"), Hit("c") with { Name = "c2" }];
        await Service().RunAsync("u@x.com", "b!me", second.State, false, default);
        Assert.Equal(2, api.Lookups);

        // A folder above a moved: its web URL changed
        api.Hits = [Hit("a") with { WebUrl = "https://x/Docs/New/a" }, Hit("b"), Hit("c")];
        api.Lookups = 0;
        await Service().RunAsync("u@x.com", "b!me", second.State, false, default);
        Assert.Equal(1, api.Lookups);

        // After a week, or on Refresh, every item
        now += TimeSpan.FromDays(8);
        api.Lookups = 0;
        await Service().RunAsync("u@x.com", "b!me", second.State, false, default);
        Assert.Equal(3, api.Lookups);
        api.Lookups = 0;
        await Service().RunAsync("u@x.com", "b!me", first.State with { RunAt = now }, recheck: true, default);
        Assert.Equal(3, api.Lookups);
    }

    [Fact]
    public async Task Pages_are_read_until_search_has_no_more()
    {
        api.Hits = [.. Enumerable.Range(0, 5).Select(i => Hit($"f{i}"))];

        var result = await Service(pageSize: 2).RunAsync("u@x.com", "b!me", null, false, default);

        Assert.Equal(5, result.State.Items.Count);
        Assert.Equal(3, api.Searches);
    }

    [Fact]
    public async Task An_item_search_stops_returning_goes_at_the_second_run_without_it()
    {
        api.Hits = [Hit("a"), Hit("b")];
        var first = await Service().RunAsync("u@x.com", "b!me", null, false, default);

        api.Hits = [Hit("a")];
        var second = await Service().RunAsync("u@x.com", "b!me", first.State, false, default);
        Assert.Equal(["b!alice|a", "b!alice|b"], second.State.Items.Select(i => i.Key));
        Assert.Equal(1, second.State.Items.Single(i => i.ItemId == "b").Misses);

        var third = await Service().RunAsync("u@x.com", "b!me", second.State, false, default);
        Assert.Equal(["b!alice|a"], third.State.Items.Select(i => i.Key));

        // Returned again in between: counting starts over
        api.Hits = [Hit("a"), Hit("b")];
        var back = await Service().RunAsync("u@x.com", "b!me", second.State, false, default);
        Assert.Equal(0, back.State.Items.Single(i => i.ItemId == "b").Misses);
    }

    [Fact]
    public async Task A_failed_search_keeps_the_last_list()
    {
        api.Hits = [Hit("a")];
        var first = await Service().RunAsync("u@x.com", "b!me", null, false, default);
        api.SearchFails = new HttpRequestException("offline");

        var failed = await Service().RunAsync("u@x.com", "b!me", first.State, false, default);

        Assert.Equal("offline", failed.SearchError);
        Assert.Same(first.State, failed.State);
    }

    [Fact]
    public async Task An_item_gone_since_search_is_left_out_and_one_that_fails_keeps_its_last_place()
    {
        api.Hits = [Hit("a"), Hit("b")];
        var first = await Service().RunAsync("u@x.com", "b!me", null, false, default);
        api.Locations["b!alice|a"] = null; // 404: deleted or no longer shared, search lags behind

        now += TimeSpan.FromDays(8);
        var second = await Service().RunAsync("u@x.com", "b!me", first.State, false, default);
        Assert.Equal(["b!alice|b"], second.State.Items.Select(i => i.Key));

        api.LookupFails = new HttpRequestException("timeout");
        api.Hits = [Hit("b"), Hit("new")];
        var third = await Service().RunAsync("u@x.com", "b!me", second.State, recheck: true, default);
        Assert.Equal(["b!alice|b"], third.State.Items.Select(i => i.Key));
        Assert.Equal("Docs/b", third.State.Items[0].Path);
        Assert.Equal(2, third.Errors.Count);
    }

    [Fact]
    public void Graph_search_hits_parse_into_items_with_drive_owner_and_parent()
    {
        // As Graph answered (2026-10-26), trimmed; the same hit can come twice at larger page sizes
        const string Json = """
            {"value":[{"searchTerms":["u@x.com"],"hitsContainers":[{"hits":[
              {"hitId":"01A","rank":1,"resource":{"@odata.type":"#microsoft.graph.driveItem","size":34980,
                "file":{"mimeType":"application/vnd.openxmlformats-officedocument.wordprocessingml.document"},"id":"01A",
                "createdBy":{"user":{"displayName":"Tessa F","email":"tessa@x.com"}},"lastModifiedBy":{"user":{"displayName":"Other","email":"o@x.com"}},
                "lastModifiedDateTime":"2021-06-09T11:02:00Z","name":"Letter.docx",
                "parentReference":{"driveId":"b!tessa","id":"01P","sharepointIds":{"listId":"l","listItemId":"59070"},"siteId":"x-my.sharepoint.com,s,w"},
                "webUrl":"https://x-my.sharepoint.com/personal/tessa/Documents/Letter.docx"}},
              {"hitId":"01F","rank":2,"resource":{"@odata.type":"#microsoft.graph.driveItem","folder":{"childCount":2},"id":"01F","name":"Plans",
                "parentReference":{"driveId":"b!tessa","id":"01P"}}},
              {"hitId":"01N","rank":3,"resource":{"@odata.type":"#microsoft.graph.driveItem","package":{"type":"oneNote"},"id":"01N","name":"Notebook",
                "parentReference":{"driveId":"b!tessa"}}}
            ],"total":3,"moreResultsAvailable":true}]}]}
            """;
        using var doc = JsonDocument.Parse(Json);

        var page = SharedWithMeApi.ParseSearch(doc.RootElement);

        Assert.True(page.More);
        Assert.Equal(3, page.Hits);
        Assert.Equal(2, page.Items.Count); // the notebook is neither file nor folder
        var file = page.Items[0];
        Assert.Equal(("b!tessa", "01A", "Letter.docx", false, "01P"), (file.DriveId, file.ItemId, file.Name, file.IsFolder, file.ParentId));
        Assert.Equal(("Tessa F", "tessa@x.com", 34980L), (file.OwnerName, file.OwnerEmail, file.Size));
        Assert.Equal(new DateTimeOffset(2021, 6, 9, 11, 2, 0, TimeSpan.Zero), file.Modified);
        Assert.True(page.Items[1].IsFolder);
        Assert.Null(page.Items[1].Path);
    }

    [Fact]
    public void The_search_asks_for_driveItems_shared_with_the_user()
    {
        using var doc = JsonDocument.Parse(SharedWithMeApi.SearchBody("u@x.com", 200, 100));
        var request = doc.RootElement.GetProperty("requests")[0];

        Assert.Equal("driveItem", request.GetProperty("entityTypes")[0].GetString());
        Assert.Equal("SharedWithUsersOWSUSER:\"u@x.com\"", request.GetProperty("query").GetProperty("queryString").GetString());
        Assert.Equal((200, 100), (request.GetProperty("from").GetInt32(), request.GetProperty("size").GetInt32()));
    }

    [Theory]
    [InlineData("/drives/b!x/root:/Documenten/CV etc/Void", "Documenten/CV etc/Void")]
    [InlineData("/drives/b!x/root:", "")]
    [InlineData("/drive/root:/a%20b/c", "a b/c")]
    [InlineData("/drives/b!x/items/01P", null)]
    [InlineData(null, null)]
    public void The_path_in_the_owners_drive_comes_from_parentReference(string? path, string? expected) =>
        Assert.Equal(expected, SharedWithMeApi.ParentPath(path));

    [Theory]
    [InlineData("""{"EffectiveBasePermissions":{"High":"688","Low":"1006834415"}}""", 1006834415UL)]
    [InlineData("""{"EffectiveBasePermissions":{"High":688,"Low":138612833}}""", 138612833UL)]
    [InlineData("""{"Title":"x"}""", null)]
    public void Effective_permissions_are_read_as_text_or_number(string json, ulong? expected)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, SharedWithMeApi.Low(doc.RootElement));
    }
}
