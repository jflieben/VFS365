using System.Net;
using System.Text;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Tests.Drive;

/// <summary>Downloads ask for the listed version and reuse a download URL for a few minutes.</summary>
public sealed class DriveApiDownloadTests
{
    sealed class Tokens : ITokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");

        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => ValueTask.FromResult("token");
    }

    /// <summary>Graph for one item, and its pre-authenticated download host.</summary>
    sealed class Server : HttpMessageHandler
    {
        public List<string> ItemRequests { get; } = [];
        public int Downloads;
        public string ETag { get; set; } = "\"{AB12},3\"";
        public string CTag { get; set; } = "\"c:{AB12},2\"";
        public bool SelectDropsUrl { get; set; }
        public byte[] Content { get; set; } = Encoding.UTF8.GetBytes("hello");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.Host == "dl.test")
            {
                Interlocked.Increment(ref Downloads);
                Assert.Null(request.Headers.Authorization);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Content) });
            }
            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            ItemRequests.Add(query);
            var url = SelectDropsUrl && query.Contains("$select") ? "" : "\"@microsoft.graph.downloadUrl\":\"https://dl.test/file?tempauth=secret\",";
            var json = $$"""{{{url}}"id":"item1","name":"a.txt","size":{{Content.Length}},"file":{},"eTag":{{Json(ETag)}},"cTag":{{Json(CTag)}}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        static string Json(string text) => System.Text.Json.JsonSerializer.Serialize(text);
    }

    static async Task<string> ReadAsync(DriveApi api, string? tag, long length = 5)
    {
        await using var stream = await api.OpenReadAsync("drive1", "item1", tag, 0, length, default);
        return await new StreamReader(stream).ReadToEndAsync();
    }

    [Fact]
    public async Task The_item_is_read_once_with_select_and_its_download_url_reused()
    {
        var server = new Server();
        var api = new DriveApi(new M365Client(new HttpClient(server), new Tokens()));

        Assert.Equal("hello", await ReadAsync(api, server.CTag));
        Assert.Equal("hello", await ReadAsync(api, server.CTag));

        var request = Assert.Single(server.ItemRequests);
        Assert.Contains("$select=", request);
        Assert.Contains("@microsoft.graph.downloadUrl", request);
        Assert.Equal(2, server.Downloads);
    }

    [Fact]
    public async Task Without_the_url_under_select_the_whole_item_is_read()
    {
        var server = new Server { SelectDropsUrl = true };
        var api = new DriveApi(new M365Client(new HttpClient(server), new Tokens()));

        Assert.Equal("hello", await ReadAsync(api, server.CTag));
        Assert.Equal(2, server.ItemRequests.Count);
        Assert.DoesNotContain("$select", server.ItemRequests[1]);
    }

    [Fact]
    public async Task Another_version_than_listed_fails_before_downloading()
    {
        var server = new Server();
        var api = new DriveApi(new M365Client(new HttpClient(server), new Tokens()));

        var changed = await Assert.ThrowsAsync<ContentChangedException>(() => ReadAsync(api, "\"c:{AB12},1\""));

        Assert.True(changed.OtherVersion);
        Assert.Equal(server.CTag, changed.Current.CTag);
        Assert.Equal(0, server.Downloads);
    }

    [Fact]
    public async Task A_listed_etag_only_compares_with_the_etag()
    {
        var server = new Server();
        var api = new DriveApi(new M365Client(new HttpClient(server), new Tokens()));

        Assert.Equal("hello", await ReadAsync(api, server.ETag)); // the cTag is another kind of tag: not compared
        server.ETag = "\"{AB12},4\"";
        var later = new DriveApi(new M365Client(new HttpClient(server), new Tokens()));

        await Assert.ThrowsAsync<ContentChangedException>(() => ReadAsync(later, "\"{AB12},3\""));
    }

    [Theory]
    [InlineData("\"c:{A},2\"", false)]
    [InlineData("\"c:{A},1\"", true)]
    [InlineData("\"{A},5\"", false)]
    [InlineData("\"{A},4\"", true)]
    [InlineData("\"c:{B},1\"", false)]
    [InlineData("something else", false)]
    [InlineData(null, false)]
    public void Content_tags_differ_only_for_another_version_of_the_same_kind(string? listed, bool differs)
    {
        var current = new DriveItemInfo("i", "a.txt", false, 1, default, default, "\"{A},5\"", "\"c:{A},2\"");

        Assert.Equal(differs, ContentTags.Differs(listed, current));
    }
}
