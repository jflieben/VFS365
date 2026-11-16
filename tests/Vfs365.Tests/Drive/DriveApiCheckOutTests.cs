using System.Net;
using System.Text;
using System.Text.Json;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Tests.Drive;

/// <summary>
/// Libraries with required columns: saves go by upload session, so SharePoint doesn't leave the file checked out; files with such a column
/// empty are reported; a check-out seen all the same is checked in unless it is the user's own; nothing here fails the upload.
/// </summary>
public sealed class DriveApiCheckOutTests : IDisposable
{
    readonly string file = Path.Combine(Path.GetTempPath(), "vfs365-tests", $"{Guid.NewGuid():N}.txt");
    DateTimeOffset now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    public DriveApiCheckOutTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "content");
    }

    public void Dispose() => File.Delete(file);

    sealed class Tokens : ITokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");

        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => ValueTask.FromResult("token");
    }

    const string Types = """{"value":[{"id":"0x0120","name":"Folder","columns":[{"name":"FileLeafRef","required":true},{"name":"FolderOnly","required":true}]},""";
    const string Plain = Types + """{"id":"0x0101","name":"Document","columns":[{"name":"FileLeafRef","required":true}]}]}""";
    const string WithRequired = Types + """{"id":"0x0101","name":"Document","columns":[{"name":"FileLeafRef","required":true},{"name":"Needed","displayName":"Needed one","required":true}]}]}""";

    sealed class Server : HttpMessageHandler
    {
        public string Library { get; set; } = Plain;
        public string Level { get; set; } = "published";
        public string Fields { get; set; } = """{"FileLeafRef":"a.txt"}""";
        public HttpStatusCode? FailLookup, FailListItem, FailCheckIn, FailItem;
        public Action? DuringCheckIn;
        public string ETag { get; private set; } = "\"{A},1\"";
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            Requests.Add($"{request.Method} {request.RequestUri.Host}{request.RequestUri.PathAndQuery}");
            ct.ThrowIfCancellationRequested();
            if (request.RequestUri.Host == "upload.test")
            {
                await request.Content!.ReadAsByteArrayAsync(ct);
                ETag = "\"{A},2\"";
                return Json(Item(""));
            }
            if (request.Method == HttpMethod.Put && path.EndsWith("/content", StringComparison.Ordinal))
            {
                await request.Content!.ReadAsByteArrayAsync(ct);
                ETag = "\"{A},2\"";
                return Json(Item($$""","publication":{"level":"{{Level}}","versionId":"2.0"}"""));
            }
            if (path.EndsWith("/createUploadSession", StringComparison.Ordinal))
            {
                return Json("""{"uploadUrl":"https://upload.test/session"}""");
            }
            if (path.EndsWith("/list", StringComparison.Ordinal))
            {
                return FailLookup is { } status ? new HttpResponseMessage(status) { Content = new StringContent("{}") } :
                    Json("""{"id":"l1","parentReference":{"siteId":"contoso.sharepoint.com,s1,w1"}}""");
            }
            if (path.EndsWith("/l1/contentTypes", StringComparison.Ordinal))
            {
                return Json(Library);
            }
            if (path.EndsWith("/listItem", StringComparison.Ordinal))
            {
                return FailListItem is { } status ? new HttpResponseMessage(status) { Content = new StringContent("{}") } :
                    Json($$"""{"id":"7","contentType":{"id":"0x0101","name":"Document"},"sharepointIds":{"siteUrl":"https://contoso.sharepoint.com/sites/a","siteId":"s1","webId":"w1","listId":"l1"},"fields":{{Fields}}}""");
            }
            if (path.EndsWith("/columns", StringComparison.Ordinal))
            {
                return Json("""{"value":[{"name":"FileLeafRef","displayName":"Name","required":true},{"name":"Needed","displayName":"Needed one","required":true},{"name":"Other","required":false}]}""");
            }
            if (path.EndsWith("/checkin", StringComparison.Ordinal))
            {
                DuringCheckIn?.Invoke();
                if (FailCheckIn is { } status)
                {
                    return new HttpResponseMessage(status) { Content = new StringContent("{}") };
                }
                ETag = "\"{A},3\"";
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            return FailItem is { } failed ? new HttpResponseMessage(failed) { Content = new StringContent("{}") } : Json(Item(""));
        }

        string Item(string more) =>
            $$"""{"id":"item1","name":"a.txt","size":7,"file":{},"eTag":{{JsonSerializer.Serialize(ETag)}},"cTag":"\"c:{A},1\""{{more}}}""";

        static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        public int Count(string part) => Requests.Count(r => r.Contains(part, StringComparison.Ordinal));
    }

    readonly List<FileNeedsDetails> reported = [];

    DriveApi Api(Server server)
    {
        var client = new M365Client(new HttpClient(server), new Tokens()) { Delay = (_, _) => Task.CompletedTask, Clock = () => now };
        return new DriveApi(client) { NeedsDetails = reported.Add };
    }

    static UploadTarget Existing => UploadTarget.Existing(new DriveItemInfo("item1", "a.txt", false, 7, default, default, "\"{A},1\"", "\"c:{A},1\""));

    static UploadTarget New => UploadTarget.New("docs", "a.txt") with { ParentId = "folder1" };

    [Fact]
    public async Task A_library_is_looked_up_once_a_day_and_a_save_then_costs_no_extra_call()
    {
        var server = new Server();
        var api = Api(server);

        await api.UploadAsync("drive1", Existing, file, default);
        Assert.Equal(1, server.Count("/list?"));
        Assert.Equal(1, server.Count("/contentTypes?"));
        Assert.Equal(1, server.Count("PUT "));
        Assert.Contains("publication", server.Requests.Single(r => r.StartsWith("PUT ", StringComparison.Ordinal)));

        server.Requests.Clear();
        await api.UploadAsync("drive1", Existing, file, default);
        Assert.Single(server.Requests);

        server.Requests.Clear();
        now += TimeSpan.FromDays(1);
        await api.UploadAsync("drive1", Existing, file, default);
        Assert.Equal(1, server.Count("/list?"));
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_folder_s_required_column_doesn_t_count()
    {
        var server = new Server();

        await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal(0, server.Count("/createUploadSession"));
    }

    [Fact]
    public async Task A_save_in_a_library_that_requires_something_goes_by_upload_session_and_says_what_is_empty()
    {
        var server = new Server { Library = WithRequired };

        var item = await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal(1, server.Count("/createUploadSession"));
        Assert.Equal(1, server.Count("upload.test/session"));
        Assert.Equal(0, server.Count("/content?"));
        Assert.Equal(0, server.Count("/checkin"));
        Assert.Equal("\"{A},2\"", item.ETag);
        var details = Assert.Single(reported);
        Assert.Equal(["Needed one"], details.Columns);
        Assert.False(details.StillCheckedOut);
    }

    [Fact]
    public async Task The_first_save_of_a_new_file_says_what_is_empty()
    {
        var server = new Server { Library = WithRequired };

        await Api(server).UploadAsync("drive1", New, file, default);

        Assert.Equal(1, server.Count("/content?"));
        Assert.Equal(0, server.Count("/createUploadSession"));
        Assert.Equal(["Needed one"], Assert.Single(reported).Columns);
    }

    [Fact]
    public async Task With_the_columns_filled_nothing_is_said()
    {
        var server = new Server { Library = WithRequired, Fields = """{"FileLeafRef":"a.txt","Needed":"filled"}""" };

        await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Empty(reported);
    }

    [Fact]
    public async Task An_empty_file_has_no_upload_session()
    {
        File.WriteAllBytes(file, []);
        var server = new Server { Library = WithRequired };

        await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal(0, server.Count("/createUploadSession"));
        Assert.Equal(1, server.Count("/content?"));
    }

    [Fact]
    public async Task A_progressive_upload_says_what_is_empty_too()
    {
        var server = new Server { Library = WithRequired };

        await Api(server).UploadedAsync("drive1", new DriveItemInfo("item1", "big.bin", false, 7, default, default, "\"{A},2\"", "\"c:{A},2\""), default);

        Assert.Equal(["Needed one"], Assert.Single(reported).Columns);
    }

    [Fact]
    public async Task A_lookup_that_fails_is_tried_again_after_a_while_and_the_save_goes_on()
    {
        var server = new Server { FailLookup = HttpStatusCode.Forbidden };
        var api = Api(server);

        var item = await api.UploadAsync("drive1", Existing, file, default);
        Assert.Equal("item1", item.Id);
        Assert.Equal(1, server.Count("/content?"));
        Assert.Equal(1, server.Count("/list?"));

        await api.UploadAsync("drive1", Existing, file, default);
        await api.UploadAsync("drive1", Existing, file, default);
        now += TimeSpan.FromMinutes(4);
        await api.UploadAsync("drive1", Existing, file, default);
        Assert.Equal(1, server.Count("/list?")); // not again at every other save

        now += TimeSpan.FromMinutes(1);
        await api.UploadAsync("drive1", Existing, file, default);
        Assert.Equal(2, server.Count("/list?"));
    }

    [Fact]
    public async Task A_reminder_s_check_is_one_call_against_what_the_library_is_known_to_require()
    {
        var server = new Server { Library = WithRequired };
        var api = Api(server);

        Assert.Equal(["Needed one"], await api.EmptyRequiredAsync("drive1", "item1", default));
        server.Requests.Clear();
        server.Fields = """{"FileLeafRef":"a.txt","Needed":"filled"}""";

        Assert.Empty((await api.EmptyRequiredAsync("drive1", "item1", default))!);
        Assert.Contains("/listItem?", Assert.Single(server.Requests));
    }

    [Fact]
    public async Task A_reminder_s_check_in_a_library_that_requires_nothing_costs_no_call_and_one_that_isn_t_known_says_so()
    {
        var plain = new Server();
        var api = Api(plain);
        Assert.Empty((await api.EmptyRequiredAsync("drive1", "item1", default))!);
        Assert.Equal(0, plain.Count("/listItem"));

        Assert.Null(await Api(new Server { FailLookup = HttpStatusCode.Forbidden }).EmptyRequiredAsync("drive1", "item1", default));
    }

    [Fact]
    public async Task A_lookup_that_hangs_doesn_t_hold_up_the_save()
    {
        var server = new Hanging();
        var client = new M365Client(new HttpClient(server), new Tokens()) { Delay = (_, _) => Task.CompletedTask };
        var api = new DriveApi(client) { RequiredColumnsWait = TimeSpan.FromMilliseconds(50) };

        var item = await api.UploadAsync("drive1", Existing, file, default);

        Assert.Equal("item1", item.Id);
        server.Release.TrySetResult();
    }

    sealed class Hanging : HttpMessageHandler
    {
        public TaskCompletionSource Release { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get)
            {
                await Release.Task;
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"item1","name":"a.txt","size":7,"file":{},"eTag":"\"{A},2\"","cTag":"\"c:{A},2\""}""", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public async Task A_check_out_all_the_same_with_an_empty_required_column_is_checked_in_and_read_again()
    {
        var server = new Server { Level = "checkout" };

        var item = await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal(1, server.Count("/checkin"));
        Assert.Contains(server.Requests, r => r.Contains("/v1.0/sites/contoso.sharepoint.com,s1,w1/lists/l1/contentTypes/0x0101/columns", StringComparison.Ordinal));
        Assert.Equal("\"{A},3\"", item.ETag); // the next save's If-Match
        var details = Assert.Single(reported);
        Assert.False(details.StillCheckedOut);
        Assert.Equal(["Needed one"], details.Columns);
    }

    [Fact]
    public async Task After_such_a_check_out_the_library_is_looked_up_again()
    {
        var server = new Server { Level = "checkout" };
        var api = Api(server);

        await api.UploadAsync("drive1", Existing, file, default);
        server.Library = WithRequired;
        await api.UploadAsync("drive1", Existing, file, default);

        Assert.Equal(2, server.Count("/list?"));
        Assert.Equal(1, server.Count("/createUploadSession"));
    }

    [Fact]
    public async Task A_check_out_with_the_required_columns_filled_is_the_user_s_own_and_stays()
    {
        var server = new Server { Level = "checkout", Fields = """{"FileLeafRef":"a.txt","Needed":"filled"}""" };

        var item = await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal(0, server.Count("/checkin"));
        Assert.Equal("\"{A},2\"", item.ETag);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_failed_look_at_a_check_out_keeps_the_save_and_checks_nothing_in()
    {
        var server = new Server { Level = "checkout", FailListItem = HttpStatusCode.Forbidden };

        var item = await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal("item1", item.Id);
        Assert.Equal(0, server.Count("/checkin"));
        Assert.Equal(1, server.Count("PUT "));
        var details = Assert.Single(reported);
        Assert.True(details.StillCheckedOut);
        Assert.Empty(details.Columns);
    }

    [Fact]
    public async Task A_failed_check_in_keeps_the_save_and_says_so()
    {
        var server = new Server { Level = "checkout", FailCheckIn = HttpStatusCode.Forbidden };

        var item = await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal("\"{A},2\"", item.ETag);
        Assert.Equal(1, server.Count("PUT "));
        var details = Assert.Single(reported);
        Assert.True(details.StillCheckedOut);
        Assert.Equal(["Needed one"], details.Columns);
        Assert.NotNull(details.Error);
    }

    [Fact]
    public async Task A_failed_read_after_the_check_in_keeps_the_save()
    {
        var server = new Server { Level = "checkout", FailItem = HttpStatusCode.Forbidden };

        var item = await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal("item1", item.Id);
        Assert.Equal(1, server.Count("PUT "));
        Assert.False(Assert.Single(reported).StillCheckedOut);
    }

    [Fact]
    public async Task Cancelling_after_the_upload_doesn_t_fail_it()
    {
        using var cancel = new CancellationTokenSource();
        var server = new Server { Level = "checkout", DuringCheckIn = cancel.Cancel };

        var item = await Api(server).UploadAsync("drive1", Existing, file, cancel.Token);

        Assert.Equal("item1", item.Id);
        Assert.Equal(1, server.Count("PUT "));
    }

    [Fact]
    public async Task A_failed_read_after_a_session_save_keeps_the_save()
    {
        var server = new Server { Library = WithRequired, FailListItem = HttpStatusCode.Forbidden };

        var item = await Api(server).UploadAsync("drive1", Existing, file, default);

        Assert.Equal("\"{A},2\"", item.ETag);
        Assert.Empty(reported);
    }

    [Fact]
    public async Task A_notifier_that_fails_doesn_t_fail_the_upload()
    {
        var server = new Server { Level = "checkout" };
        var api = Api(server);
        api.NeedsDetails = _ => throw new InvalidOperationException("no tray");

        var item = await api.UploadAsync("drive1", Existing, file, default);

        Assert.Equal("\"{A},3\"", item.ETag);
        Assert.Equal(1, server.Count("PUT "));
    }

    [Fact]
    public async Task A_new_file_in_a_plain_library_isn_t_looked_at()
    {
        var server = new Server { Level = "checkout" };

        await Api(server).UploadAsync("drive1", New, file, default);

        Assert.DoesNotContain("publication", server.Requests.Single(r => r.StartsWith("PUT ", StringComparison.Ordinal)));
        Assert.Equal(0, server.Count("/listItem"));
        Assert.Empty(reported);
    }

    [Theory]
    [InlineData("""{"Needed":"x"}""", false)]
    [InlineData("""{"Needed":""}""", true)]
    [InlineData("""{"Needed":null}""", true)]
    [InlineData("""{"Needed":[]}""", true)]
    [InlineData("""{}""", true)]
    [InlineData("""{"Needed":{"Label":"Alpha","TermGuid":"g","WssId":1}}""", false)] // managed metadata
    [InlineData("""{"NeededLookupId":"7"}""", false)] // a person or lookup
    [InlineData("""{"NeededLookupId":[]}""", true)]
    [InlineData("""{"Needed":0}""", false)]
    [InlineData("""{"Needed":false}""", false)]
    public void What_counts_as_empty(string fields, bool empty)
    {
        using var values = JsonDocument.Parse(fields);

        Assert.Equal(empty ? ["Needed"] : [], RequiredColumns.Empty([new RequiredColumn("Needed", "Needed")], values.RootElement));
    }

    [Theory]
    [InlineData("""{"name":"Needed","required":false}""")]
    [InlineData("""{"name":"Needed","required":true,"hidden":true}""")]
    [InlineData("""{"name":"Needed","required":true,"readOnly":true}""")]
    [InlineData("""{"name":"FileLeafRef","required":true}""")]
    public void Columns_a_user_can_t_fill_don_t_count(string column)
    {
        using var columns = JsonDocument.Parse($"[{column}]");

        Assert.Empty(RequiredColumns.In(columns.RootElement));
    }

    [Fact]
    public void Only_documents_content_types_count()
    {
        using var types = JsonDocument.Parse(Plain);

        Assert.False(RequiredColumns.Parse(types.RootElement.GetProperty("value")).Any);
    }
}
