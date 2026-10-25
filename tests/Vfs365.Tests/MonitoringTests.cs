using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Vfs365.Agent;
using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class MonitoringTests : IDisposable
{
    readonly FakeTableServer server = new();
    readonly List<string> log = [];
    readonly string statisticsFile = Path.Combine(Path.GetTempPath(), $"vfs365-statistics-{Guid.NewGuid():N}.json");

    Monitoring Create(string query = "?sv=2019-02-02&ss=t&srt=o&sp=a&sig=abc%3D") =>
        new(MonitoringTarget.Parse($"http://127.0.0.1:{server.Port}/devstoreaccount1{query}", out _)!, new DeviceInfo("PC1", "device-1", null),
            line => { lock (log) { log.Add(line); } })
        {
            User = () => "user@contoso.com",
        };

    [Fact]
    public void A_table_sas_url_gives_the_account_table_urls_and_expiry()
    {
        var target = MonitoringTarget.Parse(
            "https://m365pmonitor.table.core.windows.net/?sv=2026-02-06&ss=t&srt=o&sp=a&se=2026-10-15T16:14:28Z&st=2026-10-07T07:59:28Z&spr=https&sig=abc%3D", out var problem)!;

        Assert.Null(problem);
        Assert.Equal("m365pmonitor.table.core.windows.net", target.Account);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 16, 14, 28, TimeSpan.Zero), target.Expires);
        Assert.Equal("https://m365pmonitor.table.core.windows.net/errors?sv=2026-02-06&ss=t&srt=o&sp=a&se=2026-10-15T16:14:28Z&st=2026-10-07T07:59:28Z&spr=https&sig=abc%3D",
            target.TableUri("errors").OriginalString);
    }

    [Theory]
    [InlineData("http://example.com/?sv=1&sig=x")]
    [InlineData("https://account.table.core.windows.net/?sv=1")]
    [InlineData("not a url")]
    public void Unusable_urls_say_why(string url)
    {
        Assert.Null(MonitoringTarget.Parse(url, out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void No_url_means_no_reporting_and_no_problem()
    {
        Assert.Null(MonitoringTarget.Parse("  ", out var problem));
        Assert.Null(problem);
    }

    const string Account = "https://contoso.table.core.windows.net/";

    [Theory]
    // The portal's account SAS with Table, Object and Add, and a table SAS (Storage Explorer) with Add
    [InlineData("?sv=2022-11-02&ss=t&srt=o&sp=a&se=2027-10-24T16:00:00Z&st=2026-10-24T08:00:00Z&spr=https&sig=abc%3D")]
    [InlineData("?sv=2019-02-02&tn=errors&sp=a&se=2027-10-24T16:00:00Z&sig=abc%3D")]
    // Extra rights that can't read or change rows: creating tables, more resource types
    [InlineData("?sv=2022-11-02&ss=t&srt=sco&sp=ac&se=2027-10-24T16:00:00Z&spr=https&sig=abc%3D")]
    public void An_add_only_table_sas_is_used(string query) => Assert.NotNull(MonitoringTarget.Parse(Account + query, out _));

    [Theory]
    // The portal's default account SAS: every service, type and permission
    [InlineData("?sv=2022-11-02&ss=bfqt&srt=sco&sp=rwdlacupiytfx&se=2027-10-24T16:00:00Z&st=2026-10-24T08:00:00Z&spr=https&sig=abc%3D", "sp=rwdlacupiytfx")]
    // Table only, but without objects, or reading too (the dashboard's SAS)
    [InlineData("?sv=2022-11-02&ss=t&srt=c&sp=a&se=2027-10-24T16:00:00Z&spr=https&sig=abc%3D", "srt=c")]
    [InlineData("?sv=2022-11-02&ss=t&srt=co&sp=rl&se=2027-10-24T16:00:00Z&spr=https&sig=abc%3D", "sp=rl")]
    [InlineData("?sv=2022-11-02&ss=bt&srt=o&sp=a&se=2027-10-24T16:00:00Z&spr=https&sig=abc%3D", "ss=bt")]
    // A blob container SAS, a table SAS that can read and delete, and one whose rights are in a stored access policy
    [InlineData("?sp=racwdl&st=2026-10-24T08:00:00Z&se=2027-10-24T16:00:00Z&spr=https&sv=2022-11-02&sr=c&sig=abc%3D", "sr=c")]
    [InlineData("?sv=2019-02-02&tn=errors&sp=raud&se=2027-10-24T16:00:00Z&sig=abc%3D", "sp=raud")]
    [InlineData("?sv=2019-02-02&tn=errors&si=monitoring&sig=abc%3D", "no sp")]
    public void A_sas_that_can_do_more_than_add_rows_is_refused_and_says_why(string query, string named)
    {
        Assert.Null(MonitoringTarget.Parse(Account + query, out var problem));
        Assert.Contains(named, problem);
        Assert.Contains("ss=t, srt=o, sp=a", problem);
        Assert.DoesNotContain("sig=", problem);
    }

    [Fact]
    public async Task A_changed_monitoring_policy_applies_at_once()
    {
        var monitoring = Create();
        monitoring.Retarget(MonitoringTarget.Parse($"http://127.0.0.1:{server.Port}/devstoreaccount1?sv=2019-02-02&ss=t&srt=o&sp=a&sig=renewed", out _),
            MonitoringDetail.NoPaths);
        monitoring.Error("upload", @"\OneDrive\secret.docx: offline", @"\OneDrive\secret.docx");
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));

        var request = Assert.Single(server.Requests);
        Assert.Contains("sig=renewed", request.Line);
        Assert.DoesNotContain("secret", request.Body);

        monitoring.Retarget(null, MonitoringDetail.Full); // MonitoringUrl removed
        monitoring.Error("upload", "two");
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));
        Assert.Single(server.Requests);
        Assert.Equal("Monitoring: off", monitoring.Started);
    }

    [Fact]
    public void Entities_carry_odata_types_for_long_numbers_and_times()
    {
        var json = Monitoring.EntityJson("2026-10-07", "r1",
            [("Bytes", 5_000_000_000L), ("Count", 3), ("At", new DateTimeOffset(2026, 10, 7, 8, 0, 0, TimeSpan.Zero)), ("Empty", null), ("Name", "a")]);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("2026-10-07", root.GetProperty("PartitionKey").GetString());
        Assert.Equal("5000000000", root.GetProperty("Bytes").GetString());
        Assert.Equal("Edm.Int64", root.GetProperty("Bytes@odata.type").GetString());
        Assert.Equal(3, root.GetProperty("Count").GetInt32());
        Assert.Equal("2026-10-07T08:00:00.000Z", root.GetProperty("At").GetString());
        Assert.Equal("Edm.DateTime", root.GetProperty("At@odata.type").GetString());
        Assert.False(root.TryGetProperty("Empty", out _));
    }

    [Fact]
    public async Task An_error_is_sent_once_with_device_and_user()
    {
        var monitoring = Create();
        monitoring.Error("upload", "report.docx: offline");
        monitoring.Error("upload", "report.docx: offline");
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));

        var request = Assert.Single(server.Requests);
        Assert.Equal("POST /devstoreaccount1/errors?sv=2019-02-02&ss=t&srt=o&sp=a&sig=abc%3D HTTP/1.1", request.Line);
        Assert.Equal("application/json", request.Headers["content-type"]);
        Assert.Equal("return-no-content", request.Headers["prefer"]);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("upload", body.RootElement.GetProperty("Source").GetString());
        Assert.Equal("report.docx: offline", body.RootElement.GetProperty("Message").GetString());
        Assert.Equal("PC1", body.RootElement.GetProperty("DeviceName").GetString());
        Assert.Equal("device-1", body.RootElement.GetProperty("DeviceId").GetString());
        Assert.Equal("user@contoso.com", body.RootElement.GetProperty("User").GetString());
        Assert.Equal(2, monitoring.Errors);
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_throttling_episode_is_one_row_with_its_numbers_and_no_error()
    {
        var episodes = new ThrottleEpisodes();
        var start = new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);
        episodes.Add(new ThrottleNotice(start, 429, TimeSpan.FromSeconds(60), Graph: false, Background: true));
        episodes.Add(new ThrottleNotice(start.AddSeconds(30), 503, TimeSpan.FromSeconds(120), Graph: true, Background: false));
        Assert.Empty(episodes.Ended(start.AddMinutes(3))); // the last pause ends at 7:02:30, then 2 quiet minutes
        var episode = Assert.Single(episodes.Ended(start.AddMinutes(5)));
        Assert.Empty(episodes.Ended(start.AddMinutes(6)));

        var monitoring = Create();
        monitoring.Throttling(episode);
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        var row = body.RootElement;
        Assert.Equal("throttled", row.GetProperty("Source").GetString());
        Assert.Equal("2026-10-14T07:00:00.000Z", row.GetProperty("OccurredAt").GetString());
        Assert.Equal("2026-10-14T07:02:30.000Z", row.GetProperty("ThrottledUntil").GetString());
        Assert.Equal(2, row.GetProperty("Responses").GetInt32());
        Assert.Equal(150, row.GetProperty("HeldSeconds").GetInt32());
        Assert.Equal(1, row.GetProperty("BackgroundResponses").GetInt32());
        Assert.Equal(1, row.GetProperty("GraphResponses").GetInt32());
        Assert.Equal(120, row.GetProperty("LongestWaitSeconds").GetInt32());
        Assert.Contains("for 3 min (the pause it asked for; background work waits longer): 2 response(s) (429 x1, 503 x1)", row.GetProperty("Message").GetString());
        Assert.Equal(0, monitoring.Errors);
    }

    [Fact]
    public void A_throttled_response_after_the_quiet_time_starts_a_new_episode_even_when_a_tick_came_late()
    {
        var episodes = new ThrottleEpisodes();
        var start = new DateTimeOffset(2026, 10, 14, 7, 0, 0, TimeSpan.Zero);
        episodes.Add(new ThrottleNotice(start, 429, TimeSpan.FromSeconds(30), Graph: true, Background: true));
        // Asleep: nobody asked for the ended episode before the next throttled response, 20 minutes on
        episodes.Add(new ThrottleNotice(start.AddMinutes(20), 503, TimeSpan.FromSeconds(10), Graph: false, Background: false));

        var ended = episodes.Ended(start.AddMinutes(21));
        var first = Assert.Single(ended);
        Assert.Equal((start, 1, TimeSpan.FromSeconds(30)), (first.From, first.Responses, first.Held));

        // A stop ends the one still going on
        var last = Assert.Single(episodes.Ended(DateTimeOffset.MaxValue));
        Assert.Equal((start.AddMinutes(20), 1, 1), (last.From, last.Responses, last.Status503));
        Assert.Empty(episodes.Ended(DateTimeOffset.MaxValue));
    }
    [Fact]
    public async Task A_failure_is_logged_once_with_a_hint()
    {
        server.Status = 403;
        server.ResponseBody = """{"odata.error":{"code":"AuthorizationResourceTypeMismatch","message":{"lang":"en-US","value":"no"}}}""";
        var monitoring = Create();
        monitoring.Error("a", "one");
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));
        monitoring.Error("b", "two");
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, server.Requests.Count);
        var line = Assert.Single(log);
        Assert.Contains("srt=o", line);
        Assert.Contains("127.0.0.1", line);
        Assert.DoesNotContain("sig=", line);
    }

    [Fact]
    public async Task An_expired_sas_sends_nothing()
    {
        var monitoring = Create("?sv=2019-02-02&ss=t&srt=o&sp=a&se=2020-01-01T00:00:00Z&sig=abc");
        monitoring.Error("a", "one");
        await monitoring.DeviceAsync("install", null);
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));

        Assert.Empty(server.Requests);
        Assert.Contains("expired", Assert.Single(log));
    }

    [Fact]
    public async Task Installs_go_to_the_devices_table_by_device()
    {
        await Create().DeviceAsync("install", @"CONTOSO\ann");

        var request = Assert.Single(server.Requests);
        Assert.StartsWith("POST /devstoreaccount1/devices?", request.Line);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("device-1", body.RootElement.GetProperty("PartitionKey").GetString());
        Assert.Equal("install", body.RootElement.GetProperty("Action").GetString());
        Assert.Equal(@"CONTOSO\ann", body.RootElement.GetProperty("Users").GetString());
    }

    [Fact]
    public void Statistics_add_up_per_day_and_finished_days_are_ready()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        statistics.Fold(new ClientUsage(10, 2, 30, 1000, 500, 0, 0, 0, 0), 1, new DateTime(2026, 10, 6, 23, 0, 0));
        statistics.Fold(new ClientUsage(15, 2, 40, 1500, 500, 1, 5, 3, 4), 1, new DateTime(2026, 10, 6, 23, 2, 0));
        statistics.Fold(new ClientUsage(20, 3, 50, 2000, 600, 1, 5, 3, 6), 3, new DateTime(2026, 10, 7, 0, 10, 0));
        statistics.Save();

        var reloaded = UsageStatistics.Load(statisticsFile);
        Assert.Equal(["2026-10-06"], reloaded.Completed(new DateTime(2026, 10, 7, 9, 0, 0)));
        var day = reloaded.Days["2026-10-06"];
        Assert.Equal(15, day.GraphRequests);
        Assert.Equal(40, day.ResourceUnits);
        Assert.Equal(1500, day.BytesReceived);
        Assert.Equal(1, day.Throttled);
        Assert.Equal(1, day.Errors);
        Assert.Equal(2, day.MinutesRunning);
        Assert.Equal(3, day.BudgetWaitForegroundSeconds);
        Assert.Equal(4, day.BudgetWaitBackgroundSeconds);
        Assert.Equal(7, day.BudgetWaitSeconds);
        Assert.Equal(2, reloaded.Days["2026-10-07"].BudgetWaitBackgroundSeconds);
        Assert.Equal(5, reloaded.Days["2026-10-07"].GraphRequests);
        Assert.Equal(2, reloaded.Days["2026-10-07"].Errors);
    }

    [Fact]
    public void Sleep_does_not_count_as_minutes_running()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        var usage = new ClientUsage(0, 0, 0, 0, 0, 0, 0, 0, 0);
        statistics.Fold(usage, 0, new DateTime(2026, 10, 6, 9, 0, 0));
        statistics.Fold(usage, 0, new DateTime(2026, 10, 6, 9, 1, 0));
        statistics.Fold(usage, 0, new DateTime(2026, 10, 6, 17, 0, 0)); // woke up after a day in sleep

        Assert.Equal(1 + UsageStatistics.MaxFoldGap.TotalMinutes, statistics.Days["2026-10-06"].MinutesRunning);
    }

    [Fact]
    public void Statistics_are_written_when_counts_change_and_running_time_alone_now_and_then()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        var idle = new ClientUsage(0, 0, 0, 0, 0, 0, 0, 0, 0);
        var start = new DateTime(2026, 10, 6, 9, 0, 0);
        statistics.Fold(idle, 0, start);

        Assert.True(statistics.SaveIfChanged(start)); // a new day
        statistics.Fold(idle, 0, start.AddMinutes(1));
        Assert.False(statistics.SaveIfChanged(start.AddMinutes(1))); // a minute of running time only
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 0, start.AddMinutes(2));
        Assert.True(statistics.SaveIfChanged(start.AddMinutes(2)));
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 1, start.AddMinutes(3));
        Assert.True(statistics.SaveIfChanged(start.AddMinutes(3))); // an error
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 1, start.AddMinutes(4));
        Assert.False(statistics.SaveIfChanged(start.AddMinutes(4)));
        Assert.True(statistics.SaveIfChanged(start.AddMinutes(3) + UsageStatistics.RunningTimeSaves));
        Assert.Equal(1, UsageStatistics.Load(statisticsFile).Days["2026-10-06"].GraphRequests);
    }

    [Fact]
    public async Task A_day_that_was_not_sent_is_kept_for_the_next_try()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 5, 12, 0, 0));
        statistics.Fold(new ClientUsage(2, 0, 2, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 6, 12, 0, 0));
        statistics.Fold(new ClientUsage(3, 0, 3, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 7, 9, 0, 0));
        var now = new DateTime(2026, 10, 7, 9, 5, 0);

        var failed = await statistics.SendCompletedAsync(now, (day, _) => Task.FromResult(day == "2026-10-05"));

        Assert.Equal(1, failed);
        Assert.Equal(["2026-10-06"], UsageStatistics.Load(statisticsFile).Completed(now));
        Assert.Equal(0, await statistics.SendCompletedAsync(now, (_, _) => Task.FromResult(true)));
        Assert.Empty(statistics.Completed(now));
        Assert.True(statistics.Days.ContainsKey("2026-10-07"));
    }

    [Fact]
    public async Task Statistics_say_whether_they_arrived()
    {
        Assert.True(await Create().StatisticsAsync("2026-10-06", [("Day", "2026-10-06")]));
        server.Status = 404;
        server.ResponseBody = """{"odata.error":{"code":"TableNotFound"}}""";
        Assert.False(await Create().StatisticsAsync("2026-10-06", [("Day", "2026-10-06")]));
    }

    [Fact]
    public void No_paths_hashes_paths_and_shortens_urls()
    {
        var message = Monitoring.Scrub(
            @"\Sites\Finance\Documents\Budget 2027.xlsx was changed or locked in Microsoft 365; saved as Budget 2027 (conflict).xlsx",
            [@"\Sites\Finance\Documents\Budget 2027.xlsx", "Budget 2027.xlsx", "Budget 2027 (conflict).xlsx"]);
        Assert.DoesNotContain("Budget", message);
        Assert.DoesNotContain("Finance", message);
        Assert.Contains("was changed or locked in Microsoft 365; saved as [path ", message);

        var fileSystem = Monitoring.Scrub(@"reading \Sites\HR\Salaries\2026.xlsx: IOException: The network name was not found", []);
        Assert.StartsWith("reading [path ", fileSystem);
        Assert.EndsWith(": IOException: The network name was not found", fileSystem);

        var url = Monitoring.Scrub("404 NotFound from https://graph.microsoft.com/v1.0/drives/b!x/root:/Secret%20plans.docx: itemNotFound", []);
        Assert.Equal("404 NotFound from https://graph.microsoft.com/...: itemNotFound", url);

        Assert.Equal(@"Files stay reachable at \\VFS365\ann; Restart assigns T: again.",
            Monitoring.Scrub(@"Files stay reachable at \\VFS365\ann; Restart assigns T: again.", []));
        Assert.Equal(Monitoring.Scrub(@"closing \OneDrive\a.docx: x", []), Monitoring.Scrub(@"closing \ONEDRIVE\A.DOCX: x", []));
    }

    [Fact]
    public async Task No_paths_applies_to_error_rows()
    {
        var monitoring = new Monitoring(MonitoringTarget.Parse($"http://127.0.0.1:{server.Port}/devstoreaccount1?sv=1&ss=t&srt=o&sp=a&sig=abc", out _)!,
            new DeviceInfo("PC1", "device-1", null), _ => { }) { Detail = MonitoringDetail.NoPaths };
        monitoring.Error("upload", @"\OneDrive\Plans\takeover.docx: 503 ServiceUnavailable", @"\OneDrive\Plans\takeover.docx", "takeover.docx");
        await monitoring.FlushAsync(TimeSpan.FromSeconds(10));

        using var body = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        var message = body.RootElement.GetProperty("Message").GetString()!;
        Assert.DoesNotContain("takeover", message);
        Assert.EndsWith(": 503 ServiceUnavailable", message);
    }

    [Fact]
    public void A_day_keeps_its_user_and_version_for_a_send_before_the_first_token()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 6, 9, 0, 0), "ann@contoso.com", "0.3.2");
        statistics.Fold(new ClientUsage(2, 0, 2, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 6, 17, 0, 0), null, "0.4.0"); // updated, no token yet
        statistics.Save();

        var day = UsageStatistics.Load(statisticsFile).Days["2026-10-06"];
        Assert.Equal("ann@contoso.com", day.User);
        Assert.Equal("0.4.0", day.Version);
    }

    static readonly DeviceInfo HostA = new("AVD-A", "guid-a", "entra-a"), HostB = new("AVD-B", "guid-b", "entra-b");

    [Fact]
    public async Task A_roaming_day_is_sent_as_the_host_it_was_used_on()
    {
        // Pooled AVD: statistics.json is in the profile, so the day used on host A is sent from host B the next day
        var onA = UsageStatistics.Load(statisticsFile);
        onA.Fold(new ClientUsage(7, 0, 7, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 6, 9, 0, 0), "ann@contoso.com", "0.4.2", HostA);
        onA.Save();

        var onB = UsageStatistics.Load(statisticsFile);
        var now = new DateTime(2026, 10, 7, 9, 0, 0);
        onB.Fold(new ClientUsage(2, 0, 2, 0, 0, 0, 0, 0, 0), 0, now, "ann@contoso.com", "0.4.2", HostB);
        var monitoring = Create();
        var sentAs = new List<(string Day, string Device)>();
        Assert.Equal(0, await onB.SendCompletedAsync(now, (day, usage) =>
        {
            sentAs.Add((day, usage.DeviceOr(HostB).Name));
            return monitoring.StatisticsAsync(day, UsageStatistics.Values(day, usage), usage.User, usage.Version, usage.DeviceOr(HostB), usage.Salt);
        }));

        Assert.Equal([("2026-10-06", "AVD-A")], sentAs);
        using var body = JsonDocument.Parse(Assert.Single(server.Requests).Body);
        var row = body.RootElement;
        Assert.Equal("2026-10-06", row.GetProperty("PartitionKey").GetString());
        Assert.Equal(("AVD-A", "guid-a", "entra-a"), (row.GetProperty("DeviceName").GetString(), row.GetProperty("DeviceId").GetString(), row.GetProperty("EntraDeviceId").GetString()));
        Assert.StartsWith("guid-a_", row.GetProperty("RowKey").GetString());
        Assert.EndsWith("_" + onA.Days["2026-10-06|guid-a"].Salt, row.GetProperty("RowKey").GetString());
        Assert.Equal("7", row.GetProperty("GraphRequests").GetString());
        Assert.True(onB.Days.ContainsKey("2026-10-07|guid-b")); // today's, on B, stays
    }

    [Fact]
    public async Task A_statistics_file_of_an_earlier_version_still_works()
    {
        File.WriteAllText(statisticsFile, """{"2026-10-05":{"GraphRequests":3,"User":"ann@contoso.com"},"2026-10-06":{"GraphRequests":4}}""");
        var statistics = UsageStatistics.Load(statisticsFile);
        // Today's own day of the earlier version becomes this device's, still without a salt (its row key as it was)
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 6, 12, 0, 0), null, null, HostB);
        Assert.Equal(["2026-10-05", "2026-10-06|guid-b"], statistics.Days.Keys.Order(StringComparer.Ordinal));
        Assert.Null(statistics.Days["2026-10-06|guid-b"].Salt);
        Assert.Equal(5, statistics.Days["2026-10-06|guid-b"].GraphRequests);
        Assert.Equal("ann@contoso.com", statistics.LastUser());

        var sent = new List<(string Day, string Device, string? Salt)>();
        await statistics.SendCompletedAsync(new DateTime(2026, 10, 7, 9, 0, 0), (day, usage) =>
        {
            sent.Add((day, usage.DeviceOr(HostB).Id, usage.Salt));
            return Task.FromResult(true);
        });
        Assert.Equal([("2026-10-05", "guid-b", null), ("2026-10-06", "guid-b", null)], sent);
    }

    [Fact]
    public async Task A_day_from_before_a_long_time_off_is_still_sent()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        statistics.Fold(new ClientUsage(5, 0, 5, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 1, 17, 0, 0), null, null, HostA);
        // Ten days later (holiday): the first fold of the start comes before the first send
        var back = new DateTime(2026, 10, 11, 9, 0, 0);
        statistics.Fold(new ClientUsage(6, 0, 6, 0, 0, 0, 0, 0, 0), 0, back, null, null, HostA);

        var sent = new List<string>();
        await statistics.SendCompletedAsync(back, (day, _) =>
        {
            sent.Add(day);
            return Task.FromResult(true);
        });
        Assert.Equal(["2026-10-01"], sent);
    }

    [Fact]
    public async Task A_day_whose_sends_fail_for_a_week_is_dropped()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 1, 17, 0, 0), null, null, HostA);
        var firstTry = new DateTime(2026, 10, 2, 9, 0, 0);
        Assert.Equal(1, await statistics.SendCompletedAsync(firstTry, (_, _) => Task.FromResult(false)));
        Assert.Equal(1, await statistics.SendCompletedAsync(firstTry.AddDays(6), (_, _) => Task.FromResult(false)));
        Assert.Single(UsageStatistics.Load(statisticsFile).Completed(firstTry.AddDays(6)));

        Assert.Equal(1, await statistics.SendCompletedAsync(firstTry + UsageStatistics.KeepTrying, (_, _) => Task.FromResult(false)));
        Assert.Empty(UsageStatistics.Load(statisticsFile).Completed(firstTry.AddDays(8)));
    }

    [Fact]
    public async Task A_day_created_again_after_a_time_zone_change_gets_a_row_of_its_own()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        statistics.Fold(new ClientUsage(1, 0, 1, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 6, 22, 0, 0), null, null, HostA);
        var first = statistics.Days["2026-10-06|guid-a"].Salt;
        await statistics.SendCompletedAsync(new DateTime(2026, 10, 7, 0, 30, 0), (_, _) => Task.FromResult(true));

        // Flown west: the local clock is back on the 6th
        statistics.Fold(new ClientUsage(2, 0, 2, 0, 0, 0, 0, 0, 0), 0, new DateTime(2026, 10, 6, 20, 0, 0), null, null, HostA);
        var again = statistics.Days["2026-10-06|guid-a"].Salt;

        Assert.NotNull(first);
        Assert.NotNull(again);
        Assert.NotEqual(first, again); // a new row key: the add isn't taken for a re-send (409) and dropped
        Assert.Equal(16, first!.Length); // random: nobody can add the day's row first
    }

    static DateTimeOffset Utc(int day, int hour, int minute) => new(2026, 10, day, hour, minute, 0, TimeSpan.Zero);

    /// <summary>A fold on a device two hours ahead of UTC: its local 2026-10-06 starts at 2026-10-05 22:00 UTC.</summary>
    static void FoldAt(UsageStatistics statistics, long resourceUnits, DateTimeOffset utc) =>
        statistics.Fold(new ClientUsage(0, 0, resourceUnits, 0, 0, 0, 0, 0, 0), 0, utc.ToOffset(TimeSpan.FromHours(2)).DateTime, utcNow: utc);

    [Fact]
    public void Resource_units_go_to_utc_hours_across_midnight_utc()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        FoldAt(statistics, 10, Utc(5, 22, 30)); // 00:30 local
        FoldAt(statistics, 30, Utc(5, 23, 40));
        FoldAt(statistics, 35, Utc(6, 0, 10)); // midnight UTC, the same local day
        FoldAt(statistics, 42, Utc(6, 21, 50)); // 23:50 local
        FoldAt(statistics, 45, Utc(6, 22, 20)); // the next local day

        var day = statistics.Days["2026-10-06"];
        Assert.Equal(Utc(5, 22, 0), day.ResourceUnitsFromHour);
        Assert.Equal([10, 20, 5, .. new long[20], 7], day.ResourceUnitsByHour);
        Assert.Equal(42, day.ResourceUnits);
        var next = statistics.Days["2026-10-07"];
        Assert.Equal(Utc(6, 22, 0), next.ResourceUnitsFromHour);
        Assert.Equal([3], next.ResourceUnitsByHour);
    }

    [Fact]
    public void The_hours_and_the_peak_minute_carry_on_after_a_restart_mid_day()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        FoldAt(statistics, 5, Utc(6, 9, 0));
        FoldAt(statistics, 45, Utc(6, 9, 1)); // 40 in a minute
        FoldAt(statistics, 57, Utc(6, 9, 2));
        FoldAt(statistics, 157, Utc(6, 9, 12)); // 100 in 10 minutes (asleep): 10 a minute
        statistics.Save();

        // A new agent: its client counts from 0 again
        var restarted = UsageStatistics.Load(statisticsFile);
        Assert.Equal((40, Utc(6, 9, 1)), (restarted.Days["2026-10-06"].PeakMinuteResourceUnits, restarted.Days["2026-10-06"].PeakMinuteAt));
        FoldAt(restarted, 30, Utc(6, 10, 5)); // a start's first fold counts as one minute
        FoldAt(restarted, 80, Utc(6, 10, 6)); // 50 in a minute
        FoldAt(restarted, 81, Utc(6, 10, 7));
        restarted.Save();

        var day = UsageStatistics.Load(statisticsFile).Days["2026-10-06"];
        Assert.Equal(157 + 81, day.ResourceUnits);
        Assert.Equal(Utc(6, 9, 0), day.ResourceUnitsFromHour);
        Assert.Equal([157, 81], day.ResourceUnitsByHour);
        Assert.Equal((50, Utc(6, 10, 6)), (day.PeakMinuteResourceUnits, day.PeakMinuteAt));
    }

    [Fact]
    public void A_clock_set_back_keeps_the_hours_in_order()
    {
        var statistics = UsageStatistics.Load(statisticsFile);
        FoldAt(statistics, 4, Utc(6, 10, 0));
        FoldAt(statistics, 6, Utc(6, 8, 30));

        var day = statistics.Days["2026-10-06"];
        Assert.Equal(Utc(6, 8, 0), day.ResourceUnitsFromHour);
        Assert.Equal([2, 0, 4], day.ResourceUnitsByHour);
    }

    [Fact]
    public async Task Statistics_rows_carry_the_hours_and_the_peak_minute_and_older_days_leave_them_out()
    {
        File.WriteAllText(statisticsFile, """{"2026-10-05":{"ResourceUnits":3}}""");
        var statistics = UsageStatistics.Load(statisticsFile);
        FoldAt(statistics, 10, Utc(5, 22, 30));
        FoldAt(statistics, 30, Utc(5, 23, 40)); // 20 in 70 minutes
        FoldAt(statistics, 55, Utc(5, 23, 41)); // 25 in a minute
        var monitoring = Create();
        await statistics.SendCompletedAsync(new DateTime(2026, 10, 7, 9, 0, 0), (day, usage) =>
            monitoring.StatisticsAsync(day, UsageStatistics.Values(day, usage), usage.User, usage.Version, usage.DeviceOr(HostA), usage.Salt));

        var rows = new Dictionary<string, Dictionary<string, string?>>();
        foreach (var request in server.Requests)
        {
            using var body = JsonDocument.Parse(request.Body);
            rows[body.RootElement.GetProperty("PartitionKey").GetString()!] =
                body.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
        }
        var row = rows["2026-10-06"];
        Assert.Equal("10,45", row["ResourceUnitsByHour"]);
        Assert.Equal(("2026-10-05T22:00:00.000Z", "Edm.DateTime"), (row["ResourceUnitsFromHour"], row["ResourceUnitsFromHour@odata.type"]));
        Assert.Equal(("25", "Edm.Int64"), (row["PeakMinuteResourceUnits"], row["PeakMinuteResourceUnits@odata.type"]));
        Assert.Equal(("2026-10-05T23:41:00.000Z", "Edm.DateTime"), (row["PeakMinuteAt"], row["PeakMinuteAt@odata.type"]));
        Assert.Equal("55", row["ResourceUnits"]);
        var older = rows["2026-10-05"];
        Assert.Equal("3", older["ResourceUnits"]);
        Assert.DoesNotContain(older.Keys, k => k.StartsWith("ResourceUnitsByHour", StringComparison.Ordinal) || k.StartsWith("ResourceUnitsFromHour", StringComparison.Ordinal)
            || k.StartsWith("PeakMinute", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        server.Dispose();
        File.Delete(statisticsFile);
    }

    /// <summary>Answers each HTTP request with Status and ResponseBody and records it.</summary>
    sealed class FakeTableServer : IDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly List<(string Line, Dictionary<string, string> Headers, string Body)> requests = [];

        public FakeTableServer()
        {
            listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public int Status { get; set; } = 204;
        public string ResponseBody { get; set; } = "";

        public IReadOnlyList<(string Line, Dictionary<string, string> Headers, string Body)> Requests
        {
            get
            {
                lock (requests)
                {
                    return [.. requests];
                }
            }
        }

        async Task ServeAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream, Encoding.UTF8);
                    var line = await reader.ReadLineAsync() ?? "";
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var header = await reader.ReadLineAsync(); !string.IsNullOrEmpty(header); header = await reader.ReadLineAsync())
                    {
                        var colon = header.IndexOf(':');
                        headers[header[..colon].Trim()] = header[(colon + 1)..].Trim();
                    }
                    var body = new char[headers.TryGetValue("Content-Length", out var length) ? int.Parse(length) : 0];
                    await reader.ReadBlockAsync(body, 0, body.Length);
                    lock (requests)
                    {
                        requests.Add((line, headers, new string(body)));
                    }
                    var content = Encoding.UTF8.GetBytes(ResponseBody);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {Status} X\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(content);
                }
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
        }
    }
}
