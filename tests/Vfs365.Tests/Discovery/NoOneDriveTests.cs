using System.Net;
using System.Text;
using System.Text.Json;
using Vfs365.Agent;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;
using Vfs365.Tests.Drive;

namespace Vfs365.Tests.Discovery;

/// <summary>A user without a OneDrive: the tenant root comes from Graph's root site, and discovery and the drive go on without a OneDrive.</summary>
public sealed class NoOneDriveTests : IDisposable
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
        public List<string> Hosts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            Requests.Add(path);
            Hosts.Add(request.RequestUri.Host);
            var (_, status, body) = answers.First(a => path.Contains(a.Path, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    const string Drive = """{ "id": "b!me", "webUrl": "https://contoso-my.sharepoint.com/personal/ann_contoso_com/Documents" }""";
    const string RootSite = """{ "webUrl": "https://contoso.sharepoint.com" }""";
    const string NotFound = """{ "error": { "code": "ResourceNotFound", "message": "User's mysite not found" } }""";
    const string NoLicence = """{ "error": { "code": "BadRequest", "message": "Unable to retrieve user's mysite URL" } }""";
    const string NotAllowed = """{ "error": { "code": "notAllowed", "message": "You do not have access to create this personal site or you do not have a valid license" } }""";
    const string NoSpo = """{ "error": { "code": "BadRequest", "message": "Tenant does not have a SPO license." } }""";
    const string Denied = """{ "error": { "code": "accessDenied", "message": "Access denied" } }""";
    const string ReadOnly = """{ "error": { "code": "serviceReadOnly", "message": "Database Is Read Only" } }""";

    static (SharePointDiscoveryApi Api, M365Client Client) Api(Server server)
    {
        var client = new M365Client(new HttpClient(server), new Tokens()) { Delay = (_, _) => Task.CompletedTask };
        return (new SharePointDiscoveryApi(client), client);
    }

    [Fact]
    public async Task A_user_with_a_OneDrive_is_read_from_it_without_asking_for_the_root_site()
    {
        var server = new Server(("/me/drive", HttpStatusCode.OK, Drive));
        var (api, client) = Api(server);

        var home = await api.GetMyHomeAsync(default);

        Assert.Equal(("b!me", "https://contoso.sharepoint.com"), (home.OneDrive!.DriveId, home.TenantRoot));
        Assert.Equal("contoso.sharepoint.com", client.TenantRootHost);
        Assert.Equal(["/v1.0/me/drive"], server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, NotFound)]
    [InlineData(HttpStatusCode.NotFound, "")]
    [InlineData(HttpStatusCode.BadRequest, NoLicence)]
    [InlineData(HttpStatusCode.Forbidden, NotAllowed)]
    [InlineData(HttpStatusCode.BadRequest, NoSpo)]
    [InlineData(HttpStatusCode.Conflict, NoSpo)]
    public async Task A_user_without_a_OneDrive_gets_the_tenant_root_from_the_root_site(HttpStatusCode status, string body)
    {
        var server = new Server(("/me/drive", status, body), ("/sites/root", HttpStatusCode.OK, RootSite));
        var (api, client) = Api(server);

        var home = await api.GetMyHomeAsync(default);

        Assert.Null(home.OneDrive);
        Assert.Equal("https://contoso.sharepoint.com", home.TenantRoot);
        Assert.Equal("contoso.sharepoint.com", client.TenantRootHost); // tokens now go to the tenant's SharePoint hosts
        Assert.Equal(["/v1.0/me/drive", "/v1.0/sites/root"], server.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, Denied)] // consent, a policy or a lock: not "no OneDrive"
    [InlineData(HttpStatusCode.Forbidden, ReadOnly)] // SharePoint in maintenance
    [InlineData(HttpStatusCode.BadRequest, """{ "error": { "code": "invalidRequest", "message": "Invalid request" } }""")]
    [InlineData(HttpStatusCode.InternalServerError, "")]
    public async Task Other_errors_stay_errors_and_the_root_site_is_not_asked(HttpStatusCode status, string body)
    {
        var server = new Server(("/me/drive", status, body), ("/sites/root", HttpStatusCode.OK, RootSite));
        var (api, client) = Api(server);

        var e = await Assert.ThrowsAsync<M365RequestException>(() => api.GetMyHomeAsync(default));

        Assert.Equal(status, e.Status);
        Assert.Null(client.TenantRootHost);
        Assert.DoesNotContain("/v1.0/sites/root", server.Requests);
    }

    [Fact]
    public async Task When_the_root_site_cannot_be_read_either_the_OneDrive_error_stays_the_error()
    {
        var server = new Server(("/me/drive", HttpStatusCode.NotFound, NotFound), ("/sites/root", HttpStatusCode.Forbidden, Denied));
        var (api, _) = Api(server);

        var e = await Assert.ThrowsAsync<M365RequestException>(() => api.GetMyHomeAsync(default));

        Assert.Equal(HttpStatusCode.NotFound, e.Status);
        Assert.Contains("/me/drive", e.Message);
    }

    [Fact]
    public async Task A_failing_root_site_call_is_its_own_error()
    {
        var server = new Server(("/me/drive", HttpStatusCode.NotFound, NotFound), ("/sites/root", HttpStatusCode.InternalServerError, ""));
        var (api, _) = Api(server);

        var e = await Assert.ThrowsAsync<M365RequestException>(() => api.GetMyHomeAsync(default));

        Assert.Equal(HttpStatusCode.InternalServerError, e.Status);
    }

    [Theory]
    [InlineData("""{ "webUrl": "https://evil.example.com" }""")]
    [InlineData("""{ "webUrl": "http://contoso.sharepoint.com" }""")]
    [InlineData("""{ "id": "x" }""")]
    public async Task A_root_site_that_is_not_a_SharePoint_address_is_refused_and_no_token_goes_there(string body)
    {
        var server = new Server(("/me/drive", HttpStatusCode.NotFound, NotFound), ("/sites/root", HttpStatusCode.OK, body));
        var (api, client) = Api(server);

        await Assert.ThrowsAsync<InvalidDataException>(() => api.GetMyHomeAsync(default));

        Assert.Null(client.TenantRootHost);
    }

    /// <summary>Discovery for a user without a OneDrive: search at the root from the root site, the sites show, the OneDrive stays null.</summary>
    sealed class NoOneDriveApi : ISharePointDiscoveryApi
    {
        public int HomeCalls;
        public List<string> SearchedAt = [];

        public Task<MyDrive> GetMyDriveAsync(CancellationToken ct) => throw new InvalidOperationException("a user without a OneDrive has no drive to ask for");

        public Task<MyHome> GetMyHomeAsync(CancellationToken ct)
        {
            HomeCalls++;
            return Task.FromResult(new MyHome(null, "https://contoso.sharepoint.com"));
        }

        public Task<SearchPage> SearchDocumentLibrariesAsync(string tenantRoot, long afterDocId, int startRow, int rowLimit, CancellationToken ct)
        {
            SearchedAt.Add(tenantRoot);
            var library = new DiscoveredLibrary("aaaaaaaa-0000-0000-0000-000000000001", "bbbbbbbb-0000-0000-0000-000000000002", "cccccccc-0000-0000-0000-000000000003",
                $"{tenantRoot}/sites/Finance", $"{tenantRoot}/sites/Finance", "Finance", "Documents");
            return Task.FromResult(new SearchPage([library], 1, 1, [1]));
        }

        public Task<SiteMetadata> GetSiteAsync(string webUrl, CancellationToken ct) => Task.FromResult(new SiteMetadata(false, false));
        public Task<IReadOnlyList<PinnedLibrary>?> GetWebLibrariesAsync(string webUrl, CancellationToken ct) => Task.FromResult<IReadOnlyList<PinnedLibrary>?>(null);

        public Task<ListMetadata?> GetListAsync(string baseUrl, string listId, CancellationToken ct) =>
            Task.FromResult<ListMetadata?>(new ListMetadata("Documents", false, 101, false, false, null, false, false, 5));

        public Task<string> GetLibraryDriveIdAsync(LibraryEntry library, CancellationToken ct) => Task.FromResult("b!finance");
        public Task<PinnedLocation?> ResolveLocationAsync(string url, CancellationToken ct) => Task.FromResult<PinnedLocation?>(null);
    }

    static readonly DateTimeOffset Now = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Discovery_shows_the_sites_of_a_user_without_a_OneDrive_and_keeps_the_state_without_one()
    {
        var api = new NoOneDriveApi();
        var service = new DiscoveryService(api, new DiscoveryOptions { Clock = () => Now });

        var first = await service.RunAsync(null);

        Assert.Null(first.OneDrive);
        Assert.Null(first.State.OneDrive);
        Assert.Equal("https://contoso.sharepoint.com", first.State.TenantRoot);
        Assert.Equal(["https://contoso.sharepoint.com"], api.SearchedAt);
        Assert.Equal("Finance", Assert.Single(first.Libraries).SiteTitle);

        // As discovery.json holds it: the next start mounts from it, and the next run reuses what it knows
        var saved = JsonSerializer.Deserialize<DiscoveryState>(JsonSerializer.Serialize(first.State with { TenantHint = "contoso" }))!;
        Assert.True(DiscoverySchedule.Usable(saved, "contoso"));
        var second = await service.RunAsync(saved);
        Assert.Null(second.State.OneDrive);
        Assert.Single(second.Libraries);
        Assert.Equal(2, api.HomeCalls); // the OneDrive is looked for again at every run: it may have been licensed meanwhile
    }

    [Fact]
    public async Task Discovery_through_Graph_searches_at_the_root_site_of_a_user_without_a_OneDrive()
    {
        const string NoRows = """{ "PrimaryQueryResult": { "RelevantResults": { "Table": { "Rows": [] } } } }""";
        var server = new Server(("/me/drive", HttpStatusCode.NotFound, NotFound), ("/sites/root", HttpStatusCode.OK, RootSite), ("/_api/search/query", HttpStatusCode.OK, NoRows));
        var (api, _) = Api(server);

        var result = await new DiscoveryService(api, new DiscoveryOptions { Clock = () => Now }).RunAsync(null);

        Assert.Null(result.OneDrive);
        Assert.Equal("https://contoso.sharepoint.com", result.TenantRoot);
        Assert.Equal(["graph.microsoft.com", "graph.microsoft.com", "contoso.sharepoint.com"], server.Hosts);
    }

    [Fact]
    public async Task A_first_start_that_already_read_the_home_does_not_ask_again()
    {
        var api = new NoOneDriveApi();

        var result = await new DiscoveryService(api, new DiscoveryOptions { Clock = () => Now }).RunAsync(null, default, new MyHome(null, "https://contoso.sharepoint.com"));

        Assert.Equal(0, api.HomeCalls);
        Assert.Single(result.Libraries);
    }

    [Fact]
    public void A_state_with_a_tenant_root_and_no_OneDrive_is_usable_at_the_start_and_an_empty_one_is_not()
    {
        var placeholder = DiscoverySchedule.Placeholder(new MyHome(null, "https://contoso.sharepoint.com"), "contoso", DriveScope.All, Now);
        var saved = JsonSerializer.Deserialize<DiscoveryState>(JsonSerializer.Serialize(placeholder))!;

        Assert.Null(saved.OneDrive);
        Assert.Equal("https://contoso.sharepoint.com", saved.TenantRoot);
        Assert.True(DiscoverySchedule.Usable(saved, "contoso"));
        Assert.False(DiscoverySchedule.Usable(saved, "fabrikam")); // another tenant
        Assert.False(DiscoverySchedule.Usable(null, "contoso"));
        Assert.False(DiscoverySchedule.Usable(saved with { TenantRoot = "" }, "contoso")); // nothing is known
        Assert.True(DiscoverySchedule.Usable(DiscoverySchedule.Placeholder(new MyDrive("b!me", "https://contoso-my.sharepoint.com/personal/a"), "contoso", DriveScope.All, Now), "contoso"));
    }

    [Fact]
    public void A_state_saved_with_a_OneDrive_by_version_0_7_still_loads()
    {
        const string Json = """
            {"RunAt":"2026-10-29T08:00:00+00:00","TenantRoot":"https://contoso.sharepoint.com","Libraries":[],"StaticExclusions":{},"RulesVersion":1,
             "OneDrive":{"DriveId":"b!me","WebUrl":"https://contoso-my.sharepoint.com/personal/ann_contoso_com/Documents"},"TenantHint":"contoso"}
            """;

        var saved = JsonSerializer.Deserialize<DiscoveryState>(Json)!;

        Assert.Equal("b!me", saved.OneDrive!.DriveId);
        Assert.True(DiscoverySchedule.Usable(saved, "contoso"));
    }

    // The drive for a user without a OneDrive
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    async Task<string[]> RootAsync(DriveScope scope, IReadOnlyList<SharedItem>? shared = null)
    {
        var api = new FakeDriveApi();
        var library = new LibraryEntry("k-finance", "s", "w", "l", "https://contoso.sharepoint.com/sites/Finance", "Finance", "Documents", false, 1);
        var engine = new DriveEngine(
            DriveNamespace.Build(null, [library], (_, _) => Task.FromResult("b!library"), scope, shared: shared),
            api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });
        var root = (await engine.GetEntryAsync(@"\", default))!;
        return [.. (await engine.ListAsync(root, default)).Select(e => e.Name).Order(StringComparer.Ordinal)];
    }

    [Fact]
    public async Task Scope_All_shows_the_sites_without_a_OneDrive_folder()
    {
        Assert.Equal(["Sites"], await RootAsync(DriveScope.All));
        Assert.Equal(["Shared with me", "Sites"], await RootAsync(DriveScope.All, shared: []));
    }

    [Fact]
    public async Task Scope_SharePoint_is_the_sites_as_before()
    {
        Assert.Equal(["Finance"], await RootAsync(DriveScope.SharePoint));
    }

    [Fact]
    public async Task Scope_OneDrive_without_a_OneDrive_is_an_empty_drive()
    {
        Assert.Empty(await RootAsync(DriveScope.OneDrive));
    }
}
