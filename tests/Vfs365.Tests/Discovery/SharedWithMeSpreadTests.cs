using System.Text.Json;
using Vfs365.Core.Discovery;

namespace Vfs365.Tests.Discovery;

/// <summary>The shared list's call volume: lookups spread over the week, capped per run, and failed ones remembered with a back-off.</summary>
public sealed class SharedWithMeSpreadTests
{
    sealed class FakeApi : ISharedWithMeApi
    {
        readonly Lock gate = new();
        public List<SharedItem> Hits { get; set; } = [];
        public HashSet<string> Missing { get; } = [];
        public HashSet<string> Broken { get; } = [];
        public List<string> Looked { get; } = [];
        public int RightsChecks;

        public Task<SharedSearchPage> SearchAsync(string user, int from, int size, CancellationToken ct)
        {
            var page = Hits.Skip(from).Take(size).ToList();
            return Task.FromResult(new SharedSearchPage(page, from + page.Count < Hits.Count, page.Count));
        }

        public Task<SharedLocation?> GetLocationAsync(string driveId, string itemId, CancellationToken ct)
        {
            lock (gate)
            {
                Looked.Add($"{driveId}|{itemId}");
            }
            if (Broken.Contains($"{driveId}|{itemId}"))
            {
                throw new HttpRequestException("503 from graph");
            }
            return Task.FromResult<SharedLocation?>(Missing.Contains($"{driveId}|{itemId}") ? null
                : new SharedLocation($"Docs/{itemId}", itemId, Hits.FirstOrDefault(h => h.ItemId == itemId)?.ParentId ?? "parent",
                    itemId.StartsWith("folder", StringComparison.Ordinal), "https://x-my.sharepoint.com/personal/a", "l", "1"));
        }

        public Task<bool?> CanEditAsync(SharedLocation location, CancellationToken ct)
        {
            Interlocked.Increment(ref RightsChecks);
            return Task.FromResult<bool?>(true);
        }
    }

    const string User = "ann@contoso.com";
    readonly FakeApi api = new();
    DateTimeOffset now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    SharedWithMeService Service(SharedOptions? options = null) => new(api, (options ?? new SharedOptions()) with { Clock = () => now });

    static SharedItem Hit(string id, DateTimeOffset? modified = null, string? parent = "parent") =>
        new("b!alice", id, id, id.StartsWith("folder", StringComparison.Ordinal), ParentId: parent, OwnerName: "Alice", Modified: modified ?? default);

    static List<SharedItem> Files(int count) => [.. Enumerable.Range(0, count).Select(i => Hit($"file{i:000}"))];

    Task<SharedResult> RunAsync(SharedState? previous, SharedOptions? options = null, bool recheck = false, string user = User) =>
        Service(options).RunAsync(user, "b!me", previous, recheck, default);

    [Fact]
    public async Task A_run_looks_up_at_most_the_cap_newest_first_and_later_runs_do_the_rest()
    {
        var start = now;
        api.Hits = [.. Enumerable.Range(0, 250).Select(i => Hit($"file{i:000}", modified: start.AddMinutes(i)))];

        var first = await RunAsync(null);
        Assert.Equal(100, api.Looked.Count);
        Assert.Equal(150, first.Deferred);
        Assert.Equal(100, first.State.Items.Count);
        Assert.Equal(Enumerable.Range(150, 100).Select(i => $"b!alice|file{i:000}").Order(StringComparer.Ordinal), first.State.Items.Select(i => i.Key)); // the newest 100

        now += TimeSpan.FromMinutes(10);
        api.Looked.Clear();
        var second = await RunAsync(first.State);
        Assert.Equal((100, 50), (api.Looked.Count, second.Deferred));
        Assert.Equal(200, second.State.Items.Count);

        now += TimeSpan.FromMinutes(10);
        api.Looked.Clear();
        var third = await RunAsync(second.State);
        Assert.Equal((50, 0), (api.Looked.Count, third.Deferred));
        Assert.Equal(250, third.State.Items.Count);

        now += TimeSpan.FromMinutes(10);
        api.Looked.Clear();
        await RunAsync(third.State);
        Assert.Empty(api.Looked); // nothing due: a routine run is the search alone
    }

    [Fact]
    public async Task Changed_items_come_before_new_ones_and_new_ones_before_the_oldest_checks()
    {
        api.Hits = [Hit("old"), Hit("moved"), Hit("fresh")];
        var first = await RunAsync(null);
        now += TimeSpan.FromDays(8); // all three expired
        api.Hits = [Hit("old"), Hit("moved", parent: "elsewhere"), Hit("fresh"), Hit("new1", modified: now), Hit("new2", modified: now.AddMinutes(1))];
        api.Looked.Clear();

        await RunAsync(first.State, new SharedOptions { MaxLookupsPerRun = 4, Parallel = 1 });

        // Changed, then new (newest first), then the oldest check of the two expired; the other one waits
        var oldest = first.State.Items.Where(i => i.ItemId is "old" or "fresh").MinBy(i => i.CheckedAt)!;
        Assert.Equal(["b!alice|moved", "b!alice|new2", "b!alice|new1", oldest.Key], api.Looked);
    }

    [Fact]
    public async Task Moved_items_left_for_a_later_run_are_still_seen_as_moved_at_the_next_one()
    {
        api.Hits = [Hit("a"), Hit("b"), Hit("c")];
        var state = (await RunAsync(null)).State;
        api.Hits = [Hit("a", parent: "elsewhere"), Hit("b", parent: "elsewhere"), Hit("c", parent: "elsewhere")];
        var lookedPerRun = new List<int>();

        for (var run = 0; run < 5; run++)
        {
            now += TimeSpan.FromMinutes(10);
            api.Looked.Clear();
            state = (await RunAsync(state, new SharedOptions { MaxLookupsPerRun = 1 })).State;
            lookedPerRun.Add(api.Looked.Count);
        }

        Assert.Equal([1, 1, 1, 0, 0], lookedPerRun);
        Assert.All(state.Items, i => Assert.Equal("elsewhere", i.ParentId));
    }

    [Fact]
    public async Task First_reads_count_as_made_up_to_six_days_earlier_so_rechecks_spread_over_the_week()
    {
        const int Count = 300;
        api.Hits = Files(Count);
        var options = new SharedOptions { MaxLookupsPerRun = int.MaxValue };
        var state = (await RunAsync(null, options)).State;
        var start = now;
        Assert.All(state.Items, i => Assert.InRange(start - i.CheckedAt!.Value, TimeSpan.Zero, TimeSpan.FromDays(6)));

        // A run every day for five weeks: no day looks up more than a third of the items (without jitter, one day would do all of them)
        var perDay = new List<int>();
        for (var day = 1; day <= 35; day++)
        {
            now = start.AddDays(day);
            api.Looked.Clear();
            state = (await RunAsync(state, options)).State;
            perDay.Add(api.Looked.Count);
        }
        Assert.InRange(perDay.Max(), 1, Count / 3);
        Assert.Equal(0, perDay[0]); // the earliest recheck is a day after the first read
        Assert.InRange(perDay.Sum(), Count * 4, Count * 6); // about every item five times, nothing more than weekly
    }

    [Fact]
    public async Task The_spread_differs_per_user()
    {
        api.Hits = Files(200);
        var ann = (await RunAsync(null, user: "ann@contoso.com", options: new SharedOptions { MaxLookupsPerRun = int.MaxValue })).State;
        var bob = (await RunAsync(null, user: "bob@contoso.com", options: new SharedOptions { MaxLookupsPerRun = int.MaxValue })).State;

        var sameTime = ann.Items.Zip(bob.Items).Count(pair => pair.First.CheckedAt == pair.Second.CheckedAt);

        Assert.True(sameTime < 5, $"{sameTime} of 200 items have the same check time for both users");
    }

    [Fact]
    public async Task An_item_that_is_gone_is_not_looked_up_again_until_its_back_off_has_passed()
    {
        api.Hits = [Hit("a"), Hit("b")];
        api.Missing.Add("b!alice|a");
        var state = (await RunAsync(null)).State;
        Assert.Equal(["b!alice|b"], state.Items.Select(i => i.Key));
        Assert.Equal(("b!alice|a", 1), (state.Failed!.Single().Key, state.Failed!.Single().Failures));

        // Search keeps returning it (it lags behind): not looked up at the runs of the next hours
        var tried = state.Failed!.Single().TriedAt;
        foreach (var hours in new[] { 1, 3, 5 })
        {
            now = tried.AddHours(hours);
            api.Looked.Clear();
            state = (await RunAsync(state)).State;
            Assert.DoesNotContain("b!alice|a", api.Looked);
            Assert.Equal(1, state.Failed!.Single().Failures);
        }

        // After 6 hours once, after 24 hours the second time, after 4 days the third, then every 7 days
        foreach (var (failures, wait) in new[] { (1, TimeSpan.FromHours(6)), (2, TimeSpan.FromHours(24)), (3, TimeSpan.FromDays(4)), (4, TimeSpan.FromDays(7)), (5, TimeSpan.FromDays(7)) })
        {
            now = tried + wait - TimeSpan.FromMinutes(1);
            api.Looked.Clear();
            state = (await RunAsync(state)).State;
            Assert.DoesNotContain("b!alice|a", api.Looked); // the other item is read again on its own schedule

            now = tried + wait;
            state = (await RunAsync(state)).State;
            Assert.Equal(["b!alice|a"], api.Looked.Where(k => k.EndsWith("|a", StringComparison.Ordinal)));
            Assert.Equal(failures + 1, state.Failed!.Single().Failures);
            tried = now;
        }
    }

    [Fact]
    public async Task A_failing_call_is_remembered_too_and_a_known_item_keeps_its_last_place()
    {
        api.Hits = [Hit("a"), Hit("b")];
        var first = await RunAsync(null);
        now += TimeSpan.FromDays(8);
        api.Broken.Add("b!alice|a");
        api.Looked.Clear();

        var second = await RunAsync(first.State);

        Assert.Equal(["b!alice|a", "b!alice|b"], second.State.Items.Select(i => i.Key));
        Assert.Equal("Docs/a", second.State.Items[0].Path);
        Assert.Equal(first.State.Items[0].CheckedAt, second.State.Items[0].CheckedAt); // still due, not "checked now"
        Assert.Single(second.Errors);
        Assert.Equal("b!alice|a", Assert.Single(second.State.Failed!).Key);

        now += TimeSpan.FromHours(1);
        api.Looked.Clear();
        var third = await RunAsync(second.State);
        Assert.Empty(api.Looked);
        Assert.Empty(third.Errors);
        Assert.Equal("Docs/a", third.State.Items[0].Path);

        // Once the back-off has passed and the call works again, the failure is forgotten
        now += TimeSpan.FromHours(6);
        api.Broken.Clear();
        var fourth = await RunAsync(third.State);
        Assert.Equal(["b!alice|a"], api.Looked);
        Assert.Empty(fourth.State.Failed!);
        Assert.Equal(now, fourth.State.Items[0].CheckedAt);
    }

    [Fact]
    public async Task A_failure_is_forgotten_when_search_no_longer_returns_the_item()
    {
        api.Hits = [Hit("a")];
        api.Missing.Add("b!alice|a");
        var first = await RunAsync(null);
        Assert.Single(first.State.Failed!);

        api.Hits = [];
        var second = await RunAsync(first.State);

        Assert.Empty(second.State.Failed!);
    }

    [Fact]
    public async Task A_run_stops_looking_up_after_a_string_of_failed_calls()
    {
        api.Hits = Files(40);
        foreach (var hit in api.Hits)
        {
            api.Broken.Add(hit.Key);
        }

        var result = await RunAsync(null, new SharedOptions { Parallel = 1 });

        Assert.Equal(8, api.Looked.Count);
        Assert.Equal(8, result.Errors.Count);
        Assert.Equal(32, result.Deferred);
        Assert.Equal(8, result.State.Failed!.Count); // only the eight that were tried wait out a back-off; the rest are due at the next run
        Assert.Empty(result.State.Items);

        now += TimeSpan.FromMinutes(10);
        api.Broken.Clear();
        api.Looked.Clear();
        var next = await RunAsync(result.State);
        Assert.Equal(32, api.Looked.Count);
        Assert.Equal(32, next.State.Items.Count);
    }

    [Fact]
    public async Task A_success_between_failures_does_not_stop_the_run()
    {
        api.Hits = Files(30);
        foreach (var hit in api.Hits.Where((_, i) => i % 2 == 0))
        {
            api.Broken.Add(hit.Key);
        }

        var result = await RunAsync(null, new SharedOptions { Parallel = 1 });

        Assert.Equal(30, api.Looked.Count);
        Assert.Equal(15, result.State.Items.Count);
        Assert.Equal(0, result.Deferred);
    }

    [Fact]
    public async Task A_full_recheck_ignores_the_cap_and_the_back_off()
    {
        api.Hits = Files(150);
        api.Missing.Add("b!alice|file000");
        var first = await RunAsync(null, new SharedOptions { MaxLookupsPerRun = int.MaxValue });
        Assert.Contains(first.State.Failed!, f => f.Key == "b!alice|file000");
        api.Missing.Clear();
        api.Looked.Clear();

        var again = await RunAsync(first.State, recheck: true);

        Assert.Equal(150, api.Looked.Count);
        Assert.Equal(0, again.Deferred);
        Assert.Equal(150, again.State.Items.Count);
        Assert.Empty(again.State.Failed!);
    }

    [Fact]
    public async Task Rights_are_read_for_folders_only_and_a_shared_file_is_view_only()
    {
        api.Hits = [Hit("file1"), Hit("folder1")];

        var result = await RunAsync(null);

        Assert.Equal(1, api.RightsChecks);
        Assert.True(result.State.Items.Single(i => i.ItemId == "file1").ReadOnly);
        Assert.False(result.State.Items.Single(i => i.ItemId == "folder1").ReadOnly);
    }

    [Fact]
    public async Task A_list_saved_by_version_0_7_still_loads_and_runs()
    {
        // As discovery.json held it before the lookup failures were kept
        const string Json = """
            {"RunAt":"2026-10-29T08:00:00+00:00","Items":[
              {"DriveId":"b!alice","ItemId":"a","Name":"a","IsFolder":false,"Path":"Docs/a","OwnerName":"Alice","OwnerEmail":null,"Size":3,
               "Modified":"2026-10-01T00:00:00+00:00","WebUrl":null,"ParentId":"parent","ReadOnly":false,"CheckedAt":"2026-10-28T08:00:00+00:00","Misses":0}]}
            """;

        var state = JsonSerializer.Deserialize<SharedState>(Json)!;

        Assert.Null(state.Failed);
        Assert.Equal("Docs/a", Assert.Single(state.Items).Path);
        api.Hits = [Hit("a")];
        var result = await RunAsync(state);
        Assert.Equal("Docs/a", Assert.Single(result.State.Items).Path);
        Assert.Empty(api.Looked);

        // And the new list reads back as written
        var written = JsonSerializer.Deserialize<SharedState>(JsonSerializer.Serialize(new SharedState(now, state.Items, [new SharedLookupFailure("b!alice|x", now, 2)])))!;
        Assert.Equal(("b!alice|x", 2), (written.Failed!.Single().Key, written.Failed!.Single().Failures));
    }
}
