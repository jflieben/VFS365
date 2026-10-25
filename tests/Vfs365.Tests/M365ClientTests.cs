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

    /// <summary>
    /// A clock that waiting moves forward; waits of a minute or more block until <see cref="Release"/>. Elapsed is the monotonic
    /// time, Now the wall time, which <see cref="WallStep"/> can set back or forward.
    /// </summary>
    sealed class Time
    {
        static readonly DateTimeOffset Start = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        readonly Lock gate = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Elapsed = TimeSpan.FromHours(1);
        public TimeSpan WallStep;
        public DateTimeOffset Now => Start + Elapsed + WallStep;
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
                    Elapsed += wait;
                    return Task.CompletedTask;
                }
                return release.Task;
            }
        }

        public void Release(TimeSpan by)
        {
            lock (gate)
            {
                Elapsed += by;
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

    static M365Client Client(HttpMessageHandler server, Time time, double random = 0, RequestBudget? budget = null) =>
        new(new HttpClient(server), new Tokens(), budget) { Clock = () => time.Now, Monotonic = () => time.Elapsed, Random = () => random, Delay = time.Delay };

    static readonly Uri Item = new("https://graph.microsoft.com/v1.0/drives/b!x/items/1");

    static async Task<T> InBackground<T>(Func<Task<T>> work, PriorityHolder holder)
    {
        RequestPriority.Use(holder);
        return await work();
    }

    [Fact]
    public void Retry_after_is_kept_between_one_second_and_five_minutes()
    {
        var now = DateTimeOffset.UnixEpoch;
        var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(now - TimeSpan.FromHours(1));
        var huge = Status(429, TimeSpan.FromHours(3));

        Assert.Equal(TimeSpan.FromSeconds(1), M365Client.RetryAfter(past, now));
        Assert.Equal(TimeSpan.FromSeconds(1), M365Client.RetryAfter(Status(429, TimeSpan.Zero), now));
        Assert.Equal(TimeSpan.FromMinutes(5), M365Client.RetryAfter(huge, now));
        Assert.Equal(TimeSpan.FromSeconds(7), M365Client.RetryAfter(Status(503, TimeSpan.FromSeconds(7)), now));
        Assert.Null(M365Client.RetryAfter(Status(503), now));
    }

    [Fact]
    public async Task Retry_after_zero_waits_a_second_before_the_retry()
    {
        var time = new Time();
        var server = new Server(_ => Status(429, TimeSpan.Zero), _ => Ok());
        using var doc = await Client(server, time).GetJsonAsync(Item, default);

        Assert.Equal(2, server.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), time.Waits[0]);
    }

    [Fact]
    public async Task Throttle_notices_say_how_long_background_work_waits()
    {
        var notices = new List<ThrottleNotice>();
        var client = Client(new Server(_ => Status(429, TimeSpan.FromSeconds(10)), _ => Ok()), new Time { Blocking = TimeSpan.FromHours(1) });
        client.ThrottlePause = TimeSpan.FromMinutes(15);
        client.Throttled = notices.Add;

        (await client.GetJsonAsync(Item, default))!.Dispose();

        var notice = Assert.Single(notices);
        Assert.Equal(TimeSpan.FromSeconds(10), notice.Pause);
        Assert.Equal(TimeSpan.FromMinutes(15), notice.BackgroundPause);
    }

    [Fact]
    public async Task A_bad_request_is_rejected_and_not_retried()
    {
        var server = new Server(_ => Status(400), _ => Ok());
        var e = await Assert.ThrowsAsync<M365RequestException>(() => Client(server, new Time()).GetJsonAsync(Item, default));

        Assert.Equal(RemoteError.Rejected, e.Error);
        Assert.Single(server.Requests);
    }

    /// <summary>Hands each request to a script that may wait, read the body or fail.</summary>
    sealed class Scripted(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] answers) : HttpMessageHandler
    {
        int next;
        public int Requests => next;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            answers[Math.Min(Interlocked.Increment(ref next) - 1, answers.Length - 1)](request, ct);
    }

    static async Task<HttpResponseMessage> Never(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException();
    }

    static readonly TimeSpan Short = TimeSpan.FromMilliseconds(300);

    /// <summary>The agent's HttpClient has no timeout: the client's own deadlines apply.</summary>
    static M365Client Timed(HttpMessageHandler server, Time? time = null)
    {
        var clock = time ?? new Time();
        return new(new HttpClient(server) { Timeout = Timeout.InfiniteTimeSpan }, new Tokens())
        {
            HeaderTimeout = Short, StallTimeout = Short, Clock = () => clock.Now, Monotonic = () => clock.Elapsed, Random = () => 0, Delay = clock.Delay,
        };
    }

    [Fact]
    public async Task A_request_without_an_answer_fails_as_a_timeout()
    {
        var client = Timed(new Scripted(Never));

        var e = await Assert.ThrowsAsync<TaskCanceledException>(() => client.GetJsonAsync(Item, CancellationToken.None)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<TimeoutException>(e.InnerException);
        Assert.Contains("timed out", e.Message);

        // The caller's own cancellation stays a cancellation
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Timed(new Scripted(Never)).GetJsonAsync(Item, cancel.Token));
        Assert.IsNotType<TimeoutException>(cancelled.InnerException);
    }

    /// <summary>A response body that gives one byte per read after <paramref name="pause"/>, and none at all from <paramref name="stallAt"/> on.</summary>
    sealed class Dribble(byte[] data, TimeSpan pause, int stallAt = int.MaxValue) : Stream
    {
        int position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (position >= data.Length)
            {
                return 0;
            }
            await Task.Delay(position >= stallAt ? Timeout.InfiniteTimeSpan : pause, ct);
            buffer.Span[0] = data[position++];
            return 1;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Body(Stream body) => (_, _) =>
    {
        var content = new StreamContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    };

    [Fact]
    public async Task A_body_that_stops_arriving_times_out_and_a_slow_one_completes()
    {
        var json = Encoding.UTF8.GetBytes("""{"value":1}""");

        // 11 reads 60 ms apart: longer than the stall timeout in all, never in one read
        using (var doc = await Timed(new Scripted(Body(new Dribble(json, TimeSpan.FromMilliseconds(60))))).GetJsonAsync(Item, default))
        {
            Assert.Equal(1, doc!.RootElement.GetProperty("value").GetInt32());
        }

        var stalled = Timed(new Scripted(Body(new Dribble(json, TimeSpan.Zero, stallAt: 4))));
        var e = await Assert.ThrowsAsync<TaskCanceledException>(() => stalled.GetJsonAsync(Item, default)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsType<TimeoutException>(e.InnerException);

        // A download stream the same way
        var (stream, _) = await Timed(new Scripted(Body(new Dribble(new byte[8], TimeSpan.Zero, stallAt: 3)))).OpenStreamAsync(new Uri("https://contoso.sharepoint.com/download?x=1"), 0, default);
        await using (stream)
        {
            var buffer = new byte[8];
            await Assert.ThrowsAsync<TaskCanceledException>(() => stream.ReadExactlyAsync(buffer).AsTask()).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>A request body sink that takes <paramref name="pause"/> per write, and never finishes one from <paramref name="stallAt"/> bytes on.</summary>
    sealed class Sink(TimeSpan pause, long stallAt = long.MaxValue) : Stream
    {
        long written;
        public long Written => written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            await Task.Delay(written >= stallAt ? Timeout.InfiniteTimeSpan : pause, ct);
            written += buffer.Length;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Consume(Sink sink) => async (request, ct) =>
    {
        await request.Content!.CopyToAsync(sink, ct);
        return Ok();
    };

    static readonly Uri UploadUrl = new("https://contoso.sharepoint.com/_api/v2.0/drives/b!x/items/1/uploadSession?guid=abc");

    [Fact]
    public async Task An_upload_that_makes_progress_completes_and_one_that_stalls_times_out()
    {
        // 96 KiB in 16 KiB writes 100 ms apart: longer in all than the header timeout, never stalled
        var slow = new Sink(TimeSpan.FromMilliseconds(100));
        using (await Timed(new Scripted(Consume(slow))).SendJsonAsync(HttpMethod.Put, UploadUrl, () => new ByteArrayContent(new byte[96 * 1024]), default, authenticate: false))
        {
        }
        Assert.Equal(96 * 1024, slow.Written);

        var stuck = Timed(new Scripted(Consume(new Sink(TimeSpan.Zero, stallAt: 32 * 1024))));
        var e = await Assert.ThrowsAsync<TaskCanceledException>(() =>
            stuck.SendJsonAsync(HttpMethod.Put, UploadUrl, () => new ByteArrayContent(new byte[64 * 1024]), default, authenticate: false)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains("no progress", e.Message);
    }

    static Func<HttpContent> Fragment(long from, long to, long total) => () =>
    {
        var content = new ByteArrayContent(new byte[to - from + 1]);
        content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, total);
        return content;
    };

    [Fact]
    public async Task Fragments_and_other_writes_are_sent_once_and_DriveApi_resumes_them()
    {
        // A fragment whose answer got lost may have arrived: DriveApi asks the session what it expects next (DriveApiUploadTests)
        var fragment = new Scripted((_, _) => throw new HttpRequestException("connection reset"), (_, _) => Task.FromResult(Ok()));
        await Assert.ThrowsAsync<HttpRequestException>(() => Timed(fragment).SendJsonAsync(HttpMethod.Put, UploadUrl, Fragment(0, 99, 1000), default, authenticate: false));
        Assert.Equal(1, fragment.Requests);
        var write = new Scripted((_, _) => throw new HttpRequestException("connection reset"), (_, _) => Task.FromResult(Ok()));
        await Assert.ThrowsAsync<HttpRequestException>(() => Timed(write).SendJsonAsync(HttpMethod.Put, Item, () => new StringContent("x"), default));
        Assert.Equal(1, write.Requests);
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
        var budget = new RequestBudget(60, () => time.Elapsed);
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

    [Fact]
    public async Task A_wall_clock_set_back_holds_no_request()
    {
        // Throttled once (10 s), with a budget; then w32time sets the clock back 10 minutes in the middle of the pause
        var time = new Time { Blocking = TimeSpan.FromSeconds(5) };
        var budget = new RequestBudget(60, () => time.Elapsed, time.Delay);
        var client = Client(new Server(_ => Status(429, TimeSpan.FromSeconds(10)), _ => Ok()), time, budget: budget);
        var first = client.GetJsonAsync(Item, default);
        for (var i = 0; i < 200 && time.Snapshot().Length == 0; i++)
        {
            await Task.Delay(10);
        }
        time.WallStep = -TimeSpan.FromMinutes(10);
        time.Release(TimeSpan.FromSeconds(10));
        (await first.WaitAsync(TimeSpan.FromSeconds(10)))!.Dispose();

        // The pause ended after its 10 s, and neither the budget nor the release slots wait for the step
        Assert.Equal([TimeSpan.FromSeconds(10)], time.Snapshot());
        for (var i = 0; i < 20; i++)
        {
            (await client.GetJsonAsync(Item, default).WaitAsync(TimeSpan.FromSeconds(10)))!.Dispose();
        }
        Assert.Equal([TimeSpan.FromSeconds(10)], time.Snapshot());

        // Background work waits the rest of its 15 minutes, told in the new wall time; the tray's "throttled" ends on time too
        Assert.True(client.BackgroundPaused);
        Assert.Equal(time.Now + TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(10), client.BackgroundPausedUntil);
        Assert.True(client.ThrottledWithin(TimeSpan.FromMinutes(2)));
        time.Elapsed += TimeSpan.FromMinutes(2);
        Assert.False(client.ThrottledWithin(TimeSpan.FromMinutes(2)));
    }

    static byte[] Gzipped(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }
        return buffer.ToArray();
    }

    /// <summary>Answers gzip when asked for it; records whether each request asked.</summary>
    sealed class GzipServer(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<bool> AskedGzip { get; } = [];
        public int Sent;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var asked = request.Headers.AcceptEncoding.Any(e => e.Value == "gzip");
            AskedGzip.Add(asked);
            var bytes = asked ? Gzipped(body) : Encoding.UTF8.GetBytes(body);
            Sent = bytes.Length;
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            if (asked)
            {
                content.Headers.ContentEncoding.Add("gzip");
            }
            return Task.FromResult(new HttpResponseMessage(status) { Content = content });
        }
    }

    [Fact]
    public async Task Json_requests_ask_for_gzip_and_count_the_bytes_on_the_wire()
    {
        var page = $$"""{"value":[{{string.Join(',', Enumerable.Range(0, 200).Select(i => $$"""{"id":"{{i}}","name":"file {{i}}.docx"}"""))}}]}""";
        var server = new GzipServer(HttpStatusCode.OK, page);
        var client = Client(server, new Time());

        using var doc = await client.GetJsonAsync(new Uri("https://graph.microsoft.com/v1.0/drives/b!x/root/children"), default);

        Assert.Equal(200, doc!.RootElement.GetProperty("value").GetArrayLength());
        Assert.Equal([true], server.AskedGzip);
        Assert.Equal(server.Sent, client.Usage.BytesReceived);
        Assert.True(server.Sent < page.Length / 3);
    }

    [Fact]
    public async Task A_gzip_error_body_still_tells_maintenance_from_access_denied()
    {
        var server = new GzipServer(HttpStatusCode.Forbidden, """{"error":{"code":"serviceReadOnly","message":"Database Is Read Only"}}""");

        var e = await Assert.ThrowsAsync<M365RequestException>(() => Client(server, new Time()).GetJsonAsync(Item, default));

        Assert.Equal(RemoteError.ReadOnly, e.Error);
        Assert.Contains("Database Is Read Only", e.Message);
    }

    [Fact]
    public async Task Downloads_and_upload_sessions_never_ask_for_gzip()
    {
        var server = new GzipServer(HttpStatusCode.OK, "plain bytes");
        var client = Client(server, new Time());

        var (stream, _) = await client.OpenStreamAsync(new Uri("https://contoso.sharepoint.com/download.aspx?tempauth=x"), 0, default);
        await using (stream)
        {
            Assert.Equal("plain bytes", await new StreamReader(stream).ReadToEndAsync());
        }
        var status = new GzipServer(HttpStatusCode.OK, """{"nextExpectedRanges":["0-"]}""");
        (await Client(status, new Time()).SendJsonAsync(HttpMethod.Get, UploadUrl, null, default, authenticate: false))!.Dispose();

        Assert.Equal([false], server.AskedGzip);
        Assert.Equal([false], status.AskedGzip);
    }

    [Fact]
    public async Task A_503_without_retry_after_holds_background_work_a_minute_not_the_throttle_pause()
    {
        var time = new Time { Blocking = TimeSpan.FromHours(1) };
        var unwell = Client(new Server(_ => Status(503), _ => Ok()), time);
        unwell.ThrottlePause = TimeSpan.FromMinutes(15);
        var notices = new List<ThrottleNotice>();
        unwell.Throttled = notices.Add;

        (await unwell.GetJsonAsync(Item, default))!.Dispose();

        // Back-off 2 s for the retry; background work a minute from the 503
        Assert.Equal(TimeSpan.FromSeconds(2), time.Waits[0]);
        Assert.Equal(TimeSpan.FromMinutes(1), Assert.Single(notices).BackgroundPause);
        Assert.Equal(time.Now - TimeSpan.FromSeconds(2) + TimeSpan.FromMinutes(1), unwell.BackgroundPausedUntil);

        // With Retry-After it is throttling: the whole pause
        var throttled = Client(new Server(_ => Status(503, TimeSpan.FromSeconds(5)), _ => Ok()), time);
        throttled.ThrottlePause = TimeSpan.FromMinutes(15);
        (await throttled.GetJsonAsync(Item, default))!.Dispose();
        Assert.Equal(time.Now - TimeSpan.FromSeconds(5) + TimeSpan.FromMinutes(15), throttled.BackgroundPausedUntil);
    }

    [Fact]
    public async Task Overlapping_pauses_count_their_throttled_time_once()
    {
        // Two requests on their way when the tenant throttles: both answered 429 with Retry-After 10 s
        var arrived = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server = new Scripted(Throttle, Throttle, (_, _) => Task.FromResult(Ok()));
        async Task<HttpResponseMessage> Throttle(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref arrived) == 2)
            {
                both.SetResult();
            }
            await both.Task.WaitAsync(ct);
            return Status(429, TimeSpan.FromSeconds(10));
        }
        var time = new Time { Blocking = TimeSpan.FromSeconds(5) };
        var client = Client(server, time);

        var requests = new[] { client.GetJsonAsync(Item, default), client.GetJsonAsync(Item, default) };
        for (var i = 0; i < 200 && time.Snapshot().Count(w => w >= TimeSpan.FromSeconds(5)) < 2; i++)
        {
            await Task.Delay(10);
        }
        time.Release(TimeSpan.FromSeconds(10));
        foreach (var request in requests)
        {
            (await request.WaitAsync(TimeSpan.FromSeconds(10)))!.Dispose();
        }

        Assert.Equal(2, client.Usage.Throttled);
        Assert.Equal(10, client.Usage.ThrottledSeconds);
    }

    [Fact]
    public async Task A_read_whose_connection_dropped_is_sent_once_more()
    {
        static Task<HttpResponseMessage> Dropped(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.");

        var once = new Scripted(Dropped, (_, _) => Task.FromResult(Ok()));
        using (var doc = await Timed(once).GetJsonAsync(Item, default))
        {
            Assert.Equal(1, doc!.RootElement.GetProperty("value").GetInt32());
        }
        Assert.Equal(2, once.Requests);

        var twice = new Scripted(Dropped, Dropped, (_, _) => Task.FromResult(Ok()));
        await Assert.ThrowsAsync<HttpRequestException>(() => Timed(twice).GetJsonAsync(Item, default));
        Assert.Equal(2, twice.Requests);
    }

    [Fact]
    public async Task A_failed_connect_is_not_sent_again()
    {
        var refused = new Scripted((_, _) => throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made"), (_, _) => Task.FromResult(Ok()));
        await Assert.ThrowsAsync<HttpRequestException>(() => Timed(refused).GetJsonAsync(Item, default));
        Assert.Equal(1, refused.Requests);

        // The handler's connect timeout (HttpClient wraps it in its own timeout): a timeout in the handler's words, not "no answer"
        var blackHole = new Scripted((_, _) => throw new TaskCanceledException("The operation was canceled.",
            new TimeoutException("A connection could not be established within the configured ConnectTimeout.")), (_, _) => Task.FromResult(Ok()));
        var e = await Assert.ThrowsAsync<TaskCanceledException>(() => Timed(blackHole).GetJsonAsync(Item, default));
        Assert.IsType<TimeoutException>(e.InnerException);
        Assert.Contains("ConnectTimeout", e.Message);
        Assert.Contains("timed out", e.Message);
        Assert.Equal(1, blackHole.Requests);
    }

    [Fact]
    public async Task A_background_read_without_an_answer_is_sent_once_more_and_a_users_fails()
    {
        var background = new Scripted(Never, (_, _) => Task.FromResult(Ok()));
        using (await InBackground(() => Timed(background).GetJsonAsync(Item, default), new PriorityHolder()).WaitAsync(TimeSpan.FromSeconds(10)))
        {
        }
        Assert.Equal(2, background.Requests);

        var foreground = new Scripted(Never, (_, _) => Task.FromResult(Ok()));
        await Assert.ThrowsAsync<TaskCanceledException>(() => Timed(foreground).GetJsonAsync(Item, default)).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, foreground.Requests);
    }

    [Fact]
    public async Task A_transfer_url_that_reports_resource_units_is_counted()
    {
        var time = new Time();
        var budget = new RequestBudget(60, () => time.Elapsed);
        var download = new Server(_ => Ok(("x-ms-resource-unit", "1")));
        var client = Client(download, time, budget: budget);

        var (stream, _) = await client.OpenStreamAsync(new Uri("https://contoso.sharepoint.com/download.aspx?tempauth=x"), 0, default);
        await stream.DisposeAsync();

        Assert.Equal(1, client.Usage.ResourceUnits);
        Assert.False(budget.TryTake(60, background: false, out _));
    }

    [Fact]
    public void The_handler_signs_in_to_proxies_and_gives_up_connecting_after_15_seconds()
    {
        using var handler = M365Client.CreateHandler();

        Assert.Same(CredentialCache.DefaultCredentials, handler.DefaultProxyCredentials);
        Assert.True(handler.UseProxy);
        Assert.Equal(TimeSpan.FromSeconds(15), handler.ConnectTimeout);
        Assert.InRange(handler.PooledConnectionLifetime, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        Assert.InRange(handler.PooledConnectionIdleTimeout, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(4));
        Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
    }
}
