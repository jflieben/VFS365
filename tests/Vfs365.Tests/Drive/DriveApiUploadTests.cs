using System.Net;
using System.Text;
using Vfs365.Core;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Tests.Drive;

/// <summary>Upload sessions over slow or broken uplinks: resumed where the session is, started over when expired, checked before the end.</summary>
public sealed class DriveApiUploadTests : IDisposable
{
    const int MiB = 1024 * 1024;
    readonly string file = Path.Combine(Path.GetTempPath(), "vfs365-tests", $"{Guid.NewGuid():N}.bin");
    DateTimeOffset now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    sealed class Tokens : ITokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");

        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => ValueTask.FromResult("token");
    }

    /// <summary>Graph's createUploadSession and item reads, and the session's upload URL, for one item.</summary>
    sealed class Server(DriveApiUploadTests test) : HttpMessageHandler
    {
        public string ETag { get; set; } = "\"{AB12},1\"";
        public string CTag { get; set; } = "\"c:{AB12},1\"";
        public int Sessions, Puts, Cancelled;
        public List<long> FragmentLengths { get; } = [];
        public List<string?> IfMatches { get; } = [];
        public byte[] Received { get; private set; } = [];
        long next;
        bool expired, completed;

        /// <summary>Per fragment PUT (1-based): fail it before or after taking the bytes (a lost answer), or end the session.</summary>
        public Func<int, (HttpStatusCode Status, bool Stored)?>? Fail { get; set; }

        /// <summary>Per simple upload (PUT .../content, 1-based): fail it before or after taking the bytes.</summary>
        public Func<int, (HttpStatusCode Status, bool Stored)?>? FailContent { get; set; }
        public int ContentPuts;

        /// <summary>Item reads answer 503: Microsoft 365 can't be asked either.</summary>
        public bool FailItemReads { get; set; }

        /// <summary>Requests to Graph: method, path and If-Match.</summary>
        public List<(string Method, string Path, string? IfMatch)> Requests { get; } = [];

        /// <summary>Called after each PUT the session takes, with the bytes it has.</summary>
        public Action<long>? Stored { get; set; }

        /// <summary>How long each fragment takes on the wire.</summary>
        public TimeSpan PerFragment { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host == "upload.test")
            {
                if (request.Method == HttpMethod.Delete)
                {
                    Interlocked.Increment(ref Cancelled);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                if (expired || completed)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"itemNotFound\"}}") };
                }
                if (request.Method == HttpMethod.Get)
                {
                    return Json(HttpStatusCode.OK, $$"""{"nextExpectedRanges":["{{next}}-"]}""");
                }
                var puts = ++Puts;
                var range = request.Content!.Headers.ContentRange!;
                var bytes = await request.Content.ReadAsByteArrayAsync(ct);
                test.now += PerFragment;
                var failure = Fail?.Invoke(puts);
                if (failure is { Status: HttpStatusCode.NotFound })
                {
                    expired = true;
                    return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":{\"code\":\"itemNotFound\"}}") };
                }
                if (failure is { Stored: false } early)
                {
                    return new HttpResponseMessage(early.Status) { Content = new StringContent("{}") };
                }
                if (range.From != next)
                {
                    return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) { Content = new StringContent("{}") };
                }
                if (Received.Length != range.Length)
                {
                    Received = new byte[range.Length!.Value];
                }
                bytes.CopyTo(Received, range.From!.Value);
                FragmentLengths.Add(bytes.Length);
                next = range.To!.Value + 1;
                completed = next == range.Length; // the session ends with the item, a new version
                if (completed)
                {
                    NewVersion();
                }
                Stored?.Invoke(next);
                if (failure is { } late)
                {
                    return new HttpResponseMessage(late.Status) { Content = new StringContent("{}") };
                }
                return next == range.Length
                    ? Json(HttpStatusCode.Created, Item(range.Length.Value))
                    : Json(HttpStatusCode.Accepted, $$"""{"nextExpectedRanges":["{{next}}-"]}""");
            }
            var ifMatch = request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null;
            Requests.Add((request.Method.Method, uri.AbsolutePath, ifMatch));
            if (uri.AbsolutePath.EndsWith("/createUploadSession", StringComparison.Ordinal))
            {
                Sessions++;
                expired = completed = false;
                next = 0;
                IfMatches.Add(ifMatch);
                return Json(HttpStatusCode.OK, $$"""{"uploadUrl":"https://upload.test/session/{{Sessions}}"}""");
            }
            if (request.Method == HttpMethod.Put && uri.AbsolutePath.EndsWith("/content", StringComparison.Ordinal))
            {
                var bytes = await request.Content!.ReadAsByteArrayAsync(ct);
                var failure = FailContent?.Invoke(++ContentPuts);
                if (failure is { Stored: false } early)
                {
                    return new HttpResponseMessage(early.Status) { Content = new StringContent("{}") };
                }
                Received = bytes;
                NewVersion();
                return failure is { } late ? new HttpResponseMessage(late.Status) { Content = new StringContent("{}") } : Json(HttpStatusCode.Created, Item(bytes.Length));
            }
            if (request.Method == HttpMethod.Get && FailItemReads)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") };
            }
            return request.Method == HttpMethod.Delete ? new HttpResponseMessage(HttpStatusCode.NoContent) : Json(HttpStatusCode.OK, Item(Received.Length));
        }

        int version = 1;

        /// <summary>Content uploaded: the item's eTag and cTag change.</summary>
        void NewVersion()
        {
            version++;
            (ETag, CTag) = ($"\"{{AB12}},{version}\"", $"\"c:{{AB12}},{version}\"");
        }

        /// <summary>The item with what it holds, and its quickXorHash as Graph lists it.</summary>
        string Item(long size) =>
            $$"""{"id":"item1","name":"big.bin","size":{{size}},"file":{"hashes":{"quickXorHash":{{Quote(QuickXorHash.Of(Received))}} } },"eTag":{{Quote(ETag)}},"cTag":{{Quote(CTag)}}}""";

        static string Quote(string text) => System.Text.Json.JsonSerializer.Serialize(text);

        static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    DriveApi Api(Server server) =>
        new(new M365Client(new HttpClient(server), new Tokens()) { Clock = () => now, Monotonic = () => TimeSpan.FromTicks(now.UtcTicks), Delay = (_, _) => Task.CompletedTask });

    byte[] Write(int length)
    {
        var data = new byte[length];
        new Random(365).NextBytes(data);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, data);
        return data;
    }

    static UploadTarget Existing(string eTag, string cTag) =>
        UploadTarget.Existing(new DriveItemInfo("item1", "big.bin", false, 1, default, default, eTag, cTag));

    [Fact]
    public async Task A_fragment_whose_answer_got_lost_is_not_sent_again()
    {
        var data = Write(12 * MiB);
        var server = new Server(this) { Fail = put => put == 2 ? (HttpStatusCode.BadGateway, true) : null };

        var item = await Api(server).UploadAsync("drive1", UploadTarget.New("", "big.bin"), file, default);

        Assert.Equal("item1", item.Id);
        Assert.Equal(data, server.Received);
        Assert.Equal(3, server.Puts);
        Assert.Equal(1, server.Sessions);
    }

    [Fact]
    public async Task A_failed_fragment_is_sent_again_from_where_the_session_is()
    {
        var data = Write(12 * MiB);
        var server = new Server(this) { Fail = put => put is 2 or 3 ? (HttpStatusCode.InternalServerError, false) : null };

        await Api(server).UploadAsync("drive1", UploadTarget.New("", "big.bin"), file, default);

        Assert.Equal(data, server.Received);
        Assert.Equal(5, server.Puts);
        Assert.Equal(1, server.Sessions);
    }

    [Fact]
    public async Task An_expired_session_starts_over_and_never_counts_as_a_deleted_item()
    {
        var data = Write(12 * MiB);
        var server = new Server(this) { Fail = put => put == 2 ? (HttpStatusCode.NotFound, false) : null };

        var item = await Api(server).UploadAsync("drive1", Existing(server.ETag, server.CTag), file, default);

        Assert.Equal("item1", item.Id);
        Assert.Equal(data, server.Received);
        Assert.Equal(2, server.Sessions);
        Assert.All(server.IfMatches, ifMatch => Assert.Equal("\"{AB12},1\"", ifMatch));
    }

    [Fact]
    public async Task A_fragment_of_an_expired_session_fails_as_a_passing_problem()
    {
        Write(12 * MiB);
        var server = new Server(this) { Fail = _ => (HttpStatusCode.NotFound, false) };
        var api = Api(server);
        var session = await api.CreateUploadSessionAsync("drive1", UploadTarget.New("", "big.bin"), default);

        var gone = await Assert.ThrowsAsync<UploadSessionGoneException>(() => api.UploadFragmentAsync(session, file, 0, UploadSession.FragmentSize, 12 * MiB, default));

        Assert.Equal(RemoteError.Unavailable, gone.Error);
    }

    [Fact]
    public async Task An_item_changed_during_the_upload_cancels_it_before_the_last_fragment()
    {
        Write(12 * MiB);
        var server = new Server(this);
        server.Stored = _ => (server.ETag, server.CTag) = ("\"{AB12},2\"", "\"c:{AB12},2\""); // a colleague saved meanwhile

        var conflict = await Assert.ThrowsAsync<RemoteException>(() => Api(server).UploadAsync("drive1", Existing("\"{AB12},1\"", "\"c:{AB12},1\""), file, default));

        Assert.Equal(RemoteError.Conflict, conflict.Error);
        Assert.Equal(2, server.Puts);
        Assert.Equal(1, server.Cancelled);
    }

    [Fact]
    public async Task A_metadata_change_during_the_upload_lets_it_complete()
    {
        var data = Write(12 * MiB);
        var server = new Server(this);
        server.Stored = _ => server.ETag = "\"{AB12},2\""; // a column edit: new eTag, same content

        await Api(server).UploadAsync("drive1", Existing("\"{AB12},1\"", "\"c:{AB12},1\""), file, default);

        Assert.Equal(data, server.Received);
        Assert.Equal(0, server.Cancelled);
    }

    [Fact]
    public async Task Fragments_get_smaller_while_the_uplink_is_slow()
    {
        var data = Write(12 * MiB);
        var server = new Server(this) { PerFragment = TimeSpan.FromSeconds(45) };

        await Api(server).UploadAsync("drive1", UploadTarget.New("", "big.bin"), file, default);

        Assert.Equal(data, server.Received);
        Assert.Equal(new long[] { 5 * MiB, 5 * MiB / 2, 5 * MiB / 4, 5 * MiB / 8 }, server.FragmentLengths.Take(4));
        Assert.All(server.FragmentLengths.SkipLast(1), length => Assert.Equal(0, length % UploadSession.FragmentUnit));
    }

    [Fact]
    public async Task Fragments_grow_while_the_uplink_is_fast()
    {
        var data = Write(36 * MiB);
        var server = new Server(this);

        await Api(server).UploadAsync("drive1", UploadTarget.New("", "big.bin"), file, default);

        Assert.Equal(data, server.Received);
        Assert.Equal(new long[] { 5 * MiB, 5 * MiB, 5 * MiB, 5 * MiB, 10 * MiB, 6 * MiB }, server.FragmentLengths);
        Assert.Equal(20 * MiB, UploadSession.NextFragmentSize(10 * MiB, TimeSpan.FromSeconds(1), 5));
        Assert.Equal(UploadSession.MaxFragmentSize, UploadSession.NextFragmentSize(UploadSession.MaxFragmentSize, TimeSpan.Zero, 10));
        Assert.True(UploadSession.MaxFragmentSize < 60 * MiB && UploadSession.MaxFragmentSize % UploadSession.FragmentUnit == 0);
    }

    [Fact]
    public async Task The_lost_answer_to_the_last_fragment_finds_the_item_and_sends_nothing_again()
    {
        var data = Write(12 * MiB);
        var server = new Server(this) { Fail = put => put == 3 ? (HttpStatusCode.BadGateway, true) : null };

        var item = await Api(server).UploadAsync("drive1", Existing(server.ETag, server.CTag), file, default);

        // The session ended with the upload (404 when asked); a new one with the old eTag would have ended in a conflict copy
        Assert.Equal("item1", item.Id);
        Assert.Equal(data, server.Received);
        Assert.Equal(3, server.Puts);
        Assert.Equal(1, server.Sessions);
    }

    [Fact]
    public async Task A_simple_upload_that_went_through_without_an_answer_is_not_sent_again()
    {
        var data = Write(MiB);
        var server = new Server(this) { FailContent = _ => (HttpStatusCode.GatewayTimeout, true) };

        var item = await Api(server).UploadAsync("drive1", Existing(server.ETag, server.CTag), file, default);

        Assert.Equal("item1", item.Id);
        Assert.Equal(data, server.Received);
        Assert.Equal(1, server.ContentPuts);
    }

    [Fact]
    public async Task A_simple_upload_that_failed_on_the_way_is_not_taken_for_one_that_went_through()
    {
        Write(MiB);
        var server = new Server(this) { FailContent = _ => (HttpStatusCode.GatewayTimeout, false) };

        var failure = await Assert.ThrowsAsync<M365RequestException>(() => Api(server).UploadAsync("drive1", Existing(server.ETag, server.CTag), file, default));

        Assert.Equal(HttpStatusCode.GatewayTimeout, failure.Status);
    }

    [Fact]
    public async Task An_upload_whose_end_got_no_answer_and_cannot_be_looked_up_is_not_started_over()
    {
        Write(12 * MiB);
        var server = new Server(this) { Fail = put => put >= 3 ? (HttpStatusCode.BadGateway, false) : null };
        server.Stored = next => server.FailItemReads = next >= 10 * MiB;

        await Assert.ThrowsAsync<UploadUnconfirmedException>(() => Api(server).UploadAsync("drive1", UploadTarget.New("", "big.bin"), file, default));

        Assert.Equal(1, server.Sessions);
    }

    [Fact]
    public async Task Deletes_name_the_version_they_delete()
    {
        var server = new Server(this);

        await Api(server).DeleteAsync("drive1", "item1", "\"{AB12},1\"", default);

        Assert.Contains(("DELETE", "/v1.0/drives/drive1/items/item1", "\"{AB12},1\""), server.Requests);
    }

    [Fact]
    public async Task A_new_file_goes_into_its_folder_by_the_folder_s_id()
    {
        Write(1000);
        var server = new Server(this);

        await Api(server).UploadAsync("drive1", UploadTarget.New("docs", "a b.txt") with { ParentId = "folder1" }, file, default);

        Assert.Contains(server.Requests, r => r is { Method: "PUT", Path: "/v1.0/drives/drive1/items/folder1:/a%20b.txt:/content" });
    }

    public void Dispose()
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
    }
}
