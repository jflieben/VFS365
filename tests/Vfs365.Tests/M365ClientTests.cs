using System.Net;
using System.Text;
using Vfs365.Core;
using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class M365ClientTests
{
    sealed class Tokens : ITokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");
        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => ValueTask.FromResult("token");
    }

    sealed class Server(params Func<HttpRequestMessage, HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        int next;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Requests)
            {
                Requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
                return Task.FromResult(answers[Math.Min(next++, answers.Length - 1)](request));
            }
        }
    }

    /// <summary>A clock that waiting moves forward; waits of a minute or more block until <see cref="Release"/>.</summary>
    sealed class Time
    {
        readonly Lock gate = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset Now = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        public List<TimeSpan> Waits { get; } = [];
        public TimeSpan Blocking = TimeSpan.FromMinutes(1);

        public TimeSpan[] Snapshot()
        {
            lock (gate)
            {
                return [.. Waits];
            }
        }

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            lock (gate)
            {
                Waits.Add(wait);
                if (wait < Blocking)
                {
                    Now += wait;
                    return Task.CompletedTask;
                }
                return release.Task;
            }
        }

        public void Release(TimeSpan by)
        {
            lock (gate)
            {
                Now += by;
                var released = release;
                release = new(TaskCreationOptions.RunContinuationsAsynchronously);
                released.SetResult();
            }
        }
    }

    static HttpResponseMessage Ok(params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"value":1}""", Encoding.UTF8, "application/json") };
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    static HttpResponseMessage Status(int status, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}") };
        if (retryAfter is { } delay)
        {
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(delay);
        }
        return response;
    }

    static M365Client Client(Server server, Time time, double random = 0, RequestBudget? budget = null) =>
        new(new HttpClient(server), new Tokens(), budget) { Clock = () => time.Now, Random = () => random, Delay = time.Delay };

    static readonly Uri Item = new("https://graph.microsoft.com/v1.0/drives/b!x/items/1");

    static async Task<T> InBackground<T>(Func<Task<T>> work, PriorityHolder holder)
    {
        RequestPriority.Use(holder);
        return await work();
    }

    [Fact]
    public void Retry_after_is_kept_between_zero_and_five_minutes()
    {
        var now = DateTimeOffset.UnixEpoch;
        var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now - TimeSpan.FromHours(1));
        var huge = Status(429, TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.Zero, M365Client.RetryAfter(past, now));
        Assert.Equal(TimeSpan.FromMinutes(5), M365Client.RetryAfter(huge, now));
        Assert.Equal(TimeSpan.FromSeconds(7), M365Client.RetryAfter(Status(503, TimeSpan.FromSeconds(7)), now));
        Assert.Null(M365Client.RetryAfter(Status(503), now));
    }

    [Fact]
    public async Task Reads_retry_server_errors_with_jittered_backoff()
    {
        var time = new Time();
        var server = new Server(_ => Status(500), _ => Status(502), _ => Ok());
        using var doc = await Client(server, time, random: 1).GetJsonAsync(Item, default);

        Assert.Equal(3, server.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)], time.Waits);
    }

    [Fact]
    public async Task Writes_are_not_retried_on_server_errors_or_throttling_without_retry_after()
    {
        foreach (var answer in new[] { 500, 502, 504 })
        {
            var server = new Server(_ => Status(answer), _ => Ok());
            await Assert.ThrowsAsync<M365RequestException>(() => Client(server, new Time()).SendJsonAsync(HttpMethod.Put, Item, () => new StringContent("x"), default));
            Assert.Single(server.Requests);
        }
        var throttled = new Server(_ => Status(503), _ => Ok());
        await Assert.ThrowsAsync<M365RequestException>(() => Client(throttled, new Time()).SendJsonAsync(HttpMethod.Post, Item, () => new StringContent("x"), default));
        Assert.Single(throttled.Requests);
    }

    [Fact]
    public async Task Writes_are_retried_after_throttling_with_retry_after()
    {
        var time = new Time();
        var server = new Server(_ => Status(429, TimeSpan.FromSeconds(10)), _ => Ok());
        using var doc = await Client(server, time).SendJsonAsync(HttpMethod.Patch, Item, () => new StringContent("x"), default);

        Assert.Equal(2, server.Requests.Count);
        Assert.Contains(TimeSpan.FromSeconds(10), time.Waits);
    }

    [Fact]
    public async Task After_throttling_background_requests_wait_the_throttle_pause_and_foreground_ones_retry_after()
    {
        var time = new Time { Blocking = TimeSpan.FromHours(1) };
        var server = new Server(_ => Status(429, TimeSpan.FromSeconds(10)), _ => Ok());
        var client = Client(server, time, random: 0.5);
        client.ThrottlePause = TimeSpan.FromMinutes(15);

        using (await client.GetJsonAsync(Item, default))
        {
        }
        Assert.Equal(TimeSpan.FromSeconds(12.5), time.Waits[0]); // 10 s plus 25%
        Assert.True(client.BackgroundPaused);
        Assert.Equal(time.Now - TimeSpan.FromSeconds(12.5) + TimeSpan.FromMinutes(18.75), client.BackgroundPausedUntil);

        time.Waits.Clear();
        using (await InBackground(() => client.GetJsonAsync(Item, default), new PriorityHolder()))
        {
        }
        Assert.Equal(TimeSpan.FromMinutes(18.75) - TimeSpan.FromSeconds(12.5), time.Waits[0]);
        Assert.False(client.BackgroundPaused);
    }

    [Fact]
    public async Task Promoted_work_stops_waiting_for_the_background_pause()
    {
        var time = new Time();
        var server = new Server(_ => Status(429, TimeSpan.FromSeconds(10)), _ => Ok());
        var client = Client(server, time);
        using (await client.GetJsonAsync(Item, default))
        {
        }

        var holder = new PriorityHolder();
        var waiting = InBackground(() => client.GetJsonAsync(Item, default), holder);
        await Task.Delay(50);
        Assert.False(waiting.IsCompleted); // held for the 15 minutes

        holder.Promote();
        using var doc = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task Requests_held_by_a_pause_are_released_one_by_one()
    {
        var time = new Time { Blocking = TimeSpan.FromSeconds(5) };
        var server = new Server(_ => Status(429, TimeSpan.FromSeconds(10)), _ => Ok());
        var client = Client(server, time);
        async Task Held(int count)
        {
            for (var i = 0; i < 200 && time.Snapshot().Count(w => w >= TimeSpan.FromSeconds(5)) < count; i++)
            {
                await Task.Delay(10);
            }
            Assert.Equal(count, time.Snapshot().Count(w => w >= TimeSpan.FromSeconds(5)));
        }
        var first = client.GetJsonAsync(Item, default); // throttled, then held with the others
        await Held(1);
        var others = Enumerable.Range(0, 2).Select(_ => client.GetJsonAsync(Item, default)).ToList();
        await Held(3);

        time.Release(TimeSpan.FromSeconds(10));
        foreach (var request in others.Prepend(first))
        {
            (await request.WaitAsync(TimeSpan.FromSeconds(10)))!.Dispose();
        }
        // One goes at once, the others 100 ms after each other
        var spacing = time.Snapshot().Where(w => w < TimeSpan.FromSeconds(5)).ToList();
        Assert.Equal(2, spacing.Count);
        Assert.All(spacing, w => Assert.InRange(w, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(200)));
        Assert.Equal(4, server.Requests.Count);
    }

    [Fact]
    public async Task A_nearly_used_app_limit_holds_background_work_until_its_reset()
    {
        var time = new Time();
        var low = Client(new Server(_ => Ok(("RateLimit-Limit", "1250"), ("RateLimit-Remaining", "100"), ("RateLimit-Reset", "30"))), time);
        var plenty = Client(new Server(_ => Ok(("RateLimit-Limit", "1250"), ("RateLimit-Remaining", "900"), ("RateLimit-Reset", "30"))), time);

        (await low.GetJsonAsync(Item, default))!.Dispose();
        (await plenty.GetJsonAsync(Item, default))!.Dispose();

        Assert.Equal(time.Now + TimeSpan.FromSeconds(30), low.BackgroundPausedUntil);
        Assert.False(plenty.BackgroundPaused);
        Assert.Equal(DateTimeOffset.MinValue, low.LastThrottled);
    }

    [Fact]
    public async Task The_resource_unit_header_replaces_the_estimate()
    {
        var time = new Time();
        var budget = new RequestBudget(60, () => time.Now);
        var client = Client(new Server(_ => Ok(("x-ms-resource-unit", "3"))), time, budget: budget);

        (await client.GetJsonAsync(Item, default))!.Dispose();

        Assert.Equal(3, client.Usage.ResourceUnits);
        Assert.True(budget.TryTake(57, background: false, out _));
        Assert.False(budget.TryTake(1, background: false, out _));
    }

    [Fact]
    public async Task Every_attempt_counts_its_resource_units()
    {
        var client = Client(new Server(_ => Status(503, TimeSpan.FromSeconds(1)), _ => Ok()), new Time());
        (await client.GetJsonAsync(Item, default))!.Dispose();
        Assert.Equal(2, client.Usage.ResourceUnits);
    }

    [Theory]
    [InlineData("https://graph.microsoft.com/v1.0/me", null, true)]
    [InlineData("https://contoso.sharepoint.com/_api/web", null, true)]
    [InlineData("https://contoso.sharepoint.com/_api/web", "contoso.sharepoint.com", true)]
    [InlineData("https://contoso-my.sharepoint.com/_api/web", "contoso.sharepoint.com", true)]
    [InlineData("https://contosoeur.sharepoint.com/_api/web", "contoso.sharepoint.com", true)]
    [InlineData("https://contoso.sharepoint.com/_api/web", "contosoeur.sharepoint.com", true)]
    [InlineData("https://contosoapc-my.sharepoint.com/_api/web", "contosoeur.sharepoint.com", true)]
    [InlineData("https://fabrikam.sharepoint.com/_api/web", "contoso.sharepoint.com", false)]
    [InlineData("https://contosoevil.sharepoint.com/_api/web", "contoso.sharepoint.com", false)]
    [InlineData("http://contoso.sharepoint.com/_api/web", null, false)]
    [InlineData("https://contoso.sharepoint.com:8443/_api/web", null, false)]
    [InlineData("https://contoso.sharepoint.com.evil.example/_api/web", null, false)]
    [InlineData("https://evil.example/_api/web", null, false)]
    [InlineData("https://x.contoso.sharepoint.com/_api/web", null, false)]
    public void Tokens_go_only_to_graph_and_the_tenants_sharepoint(string url, string? tenantRootHost, bool expected)
    {
        Assert.Equal(expected, M365Client.MayAuthenticate(new Uri(url), tenantRootHost));
    }

    [Fact]
    public async Task A_request_to_another_host_is_refused_before_it_is_sent()
    {
        var server = new Server(_ => Ok());
        var client = Client(server, new Time());
        client.TenantRootHost = "contoso.sharepoint.com";

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetJsonAsync(new Uri("https://evil.example/_api/web"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetJsonAsync(new Uri("https://fabrikam.sharepoint.com/_api/web"), default));
        (await client.SendJsonAsync(HttpMethod.Put, new Uri("https://upload.example/session"), () => new StringContent("x"), default, authenticate: false))!.Dispose();

        Assert.Single(server.Requests);
    }
}
