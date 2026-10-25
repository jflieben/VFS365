using System.Net;
using System.Text;
using Vfs365.Core;
using Vfs365.Graph;

namespace Vfs365.Tests.Drive;

/// <summary>Folder listings: page sizes by who waits for them.</summary>
public sealed class DriveApiListingTests
{
    sealed class Tokens : ITokenSource
    {
        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");

        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct) => ValueTask.FromResult("token");
    }

    /// <summary>Two pages: the first links to the second with the $top it was asked for.</summary>
    sealed class Server : HttpMessageHandler
    {
        public List<string> Queries { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var query = Uri.UnescapeDataString(request.RequestUri!.Query);
            Queries.Add(query);
            var top = System.Text.RegularExpressions.Regex.Match(query, @"\$top=(\d+)").Groups[1].Value;
            var next = Queries.Count == 1 ? $",\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/drives/d/root/children?$top={top}&$skiptoken=abc\"" : "";
            var item = $$$"""{"id":"{{{Queries.Count}}}","name":"f{{{Queries.Count}}}.txt","size":1,"file":{}}""";
            var json = """{"value":[""" + item + "]" + next + "}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    static async Task<int> ListAsync(DriveApi api)
    {
        var items = 0;
        await foreach (var page in api.ListChildrenAsync("d", "Folder", default))
        {
            items += page.Count;
        }
        return items;
    }

    static async Task<int> InBackground(Func<Task<int>> work)
    {
        RequestPriority.MarkBackground();
        return await work();
    }

    [Fact]
    public async Task A_users_listing_starts_with_a_small_page_and_a_background_one_with_1000()
    {
        var user = new Server();
        Assert.Equal(2, await ListAsync(new DriveApi(new M365Client(new HttpClient(user), new Tokens()))));
        Assert.Contains("$top=200", user.Queries[0]);
        Assert.Contains("$top=1000", user.Queries[1]);

        var background = new Server();
        Assert.Equal(2, await InBackground(() => ListAsync(new DriveApi(new M365Client(new HttpClient(background), new Tokens())))));
        Assert.Contains("$top=1000", background.Queries[0]);
        Assert.Contains("$top=1000", background.Queries[1]);
    }
}
