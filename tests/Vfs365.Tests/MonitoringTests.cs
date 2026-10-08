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
        var monitoring = Create("?sv=2019-02-02&se=2020-01-01T00:00:00Z&sig=abc");
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
        statistics.Fold(new ClientUsage(15, 2, 40, 1500, 500, 1, 5, 3, 4), 1, new DateTime(2026, 10, 6, 23, 30, 0));
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
        Assert.Equal(30, day.MinutesRunning);
        Assert.Equal(3, day.BudgetWaitForegroundSeconds);
        Assert.Equal(4, day.BudgetWaitBackgroundSeconds);
        Assert.Equal(7, day.BudgetWaitSeconds);
        Assert.Equal(2, reloaded.Days["2026-10-07"].BudgetWaitBackgroundSeconds);
        Assert.Equal(5, reloaded.Days["2026-10-07"].GraphRequests);
        Assert.Equal(2, reloaded.Days["2026-10-07"].Errors);
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
