using System.Net;
using System.Text;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Graph;

namespace Vfs365.Tests.Discovery;

public sealed class SharePointDiscoveryApiTests
{
    sealed class Tokens : ITokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");
        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => ValueTask.FromResult("token");
    }

    /// <summary>Answers by path: the first entry whose key the request path contains.</summary>
    sealed class Server(params (string Path, HttpStatusCode Status, string Body)[] answers) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Requests.Add(path);
            var (_, status, body) = answers.First(a => path.Contains(a.Path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    const string Search = """
        { "PrimaryQueryResult": { "RelevantResults": { "Table": { "Rows": [ { "Cells": [
          { "Key": "Title", "Value": "Documents" },
          { "Key": "Path", "Value": "https://contoso.sharepoint.com/sites/Finance/Shared Documents/Forms/AllItems.aspx" },
          { "Key": "ListId", "Value": "7a9b2c4d-1111-2222-3333-444455556666" },
          { "Key": "SiteId", "Value": "aaaaaaaa-0000-0000-0000-000000000001" },
          { "Key": "WebId", "Value": "bbbbbbbb-0000-0000-0000-000000000002" },
          { "Key": "SPWebUrl", "Value": "https://contoso.sharepoint.com/sites/Finance" },
          { "Key": "SiteTitle", "Value": "Finance" },
          { "Key": "DocId", "Value": "17" }
        ] } ] } } } }
        """;

    const string List = """{ "Title": "Documents", "BaseTemplate": 101, "Hidden": false, "ItemCount": 3 }""";
    const string Denied = """{ "error": { "code": "-2147024891, System.UnauthorizedAccessException", "message": { "value": "Access denied." } } }""";
    const string Maintenance = """{ "error": { "code": "serviceReadOnly", "message": "Database Is Read Only" } }""";

    static SharePointDiscoveryApi Api(Server server) => new(new M365Client(new HttpClient(server), new Tokens()));

    [Fact]
    public async Task Libraries_of_a_site_the_user_cannot_read_show_read_write()
    {
        var server = new Server(("/_api/search/query", HttpStatusCode.OK, Search), ("/_api/site", HttpStatusCode.Forbidden, Denied),
            ("/_api/lists/GetById", HttpStatusCode.OK, List));

        var result = await new DiscoveryService(Api(server), new DiscoveryOptions())
            .RunAsync(null, default, new MyDrive("b!me", "https://contoso-my.sharepoint.com/personal/jos_contoso_com/Documents"));

        var library = Assert.Single(result.Libraries);
        Assert.Equal("Documents", library.LibraryTitle);
        Assert.False(library.ReadOnly);
        Assert.Empty(result.Errors);
        Assert.Contains(server.Requests, r => r.EndsWith("/_api/site"));
    }

    [Fact]
    public async Task A_site_in_maintenance_still_fails_its_lookup()
    {
        var api = Api(new Server(("/_api/site", HttpStatusCode.Forbidden, Maintenance)));

        var e = await Assert.ThrowsAsync<M365RequestException>(() => api.GetSiteAsync("https://contoso.sharepoint.com/sites/Finance", default));
        Assert.Equal(RemoteError.ReadOnly, e.Error);
    }
}
