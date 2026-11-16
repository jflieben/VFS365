using System.Net;
using System.Text;
using System.Text.Json;
using Vfs365.Graph;

namespace Vfs365.Tests;

/// <summary>The details window's SharePoint side: field definitions, values, how each kind is written, and refusals.</summary>
public sealed class ColumnsApiTests
{
    /// <summary>Hands out "token", and "fresh" once a claims request renewed it (as WamTokenSource keeps the renewed one).</summary>
    internal sealed class Tokens : ITokenSource
    {
        public List<string> Claims { get; } = [];

        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult(Claims.Count > 0 ? "fresh" : "token");

        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct)
        {
            Claims.Add(claims);
            return ValueTask.FromResult("fresh");
        }
    }

    /// <summary>Answers the calls of <see cref="ColumnsApi"/> for one item in one library of contoso's site a.</summary>
    internal sealed class Server : HttpMessageHandler
    {
        public const string Web = "https://contoso.sharepoint.com/sites/a";
        public List<(string Method, string Url, string? Body, string? IfMatch)> Requests { get; } = [];
        public HttpStatusCode? PatchStatus, TermStatus;

        /// <summary>The term store refuses only the token issued before the consent.</summary>
        public bool FreshTokenAllowed { get; set; }
        public string ValidateAnswer { get; set; } = """{"value":[]}""";
        public string Fields { get; set; } = """{"Title":"t","S17Text":"old","S17Number":5.0,"S17Date":"2026-11-06T10:00:00Z","S17YesNo":true,"S17Choice":"One","S17Term":{"Label":"Alpha","TermGuid":"g1","WssId":1},"_ComplianceTag":"Keep 7 years"}""";
        public string ETag { get; set; } = "\"e1,1\"";
        public string People { get; set; } = """{"S17Person":{"Name":"i:0#.f|membership|bob@contoso.com","Title":"Bob"}}""";

        public const string Definitions = """
            {"value":[
            {"InternalName":"ContentType","Title":"Content Type","TypeAsString":"Computed","Hidden":false,"ReadOnlyField":false},
            {"InternalName":"FileLeafRef","Title":"Name","TypeAsString":"File","Required":true,"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"Title","Title":"Title","TypeAsString":"Text","MaxLength":255,"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Text","Title":"Text","TypeAsString":"Text","MaxLength":50,"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Number","Title":"Number","TypeAsString":"Number","MinimumValue":-1.7976931348623157E+308,"MaximumValue":10,"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Date","Title":"Date","TypeAsString":"DateTime","DisplayFormat":0,"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17YesNo","Title":"Yes no","TypeAsString":"Boolean","Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Choice","Title":"Choice","TypeAsString":"Choice","Choices":["One","Two"],"FillInChoice":false,"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Choices","Title":"Choices","TypeAsString":"MultiChoice","Choices":["One","Two"],"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Person","Title":"Person","TypeAsString":"User","Required":true,"AllowMultipleValues":false,"SelectionMode":0,"SelectionGroup":0,"Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Term","Title":"Term","TypeAsString":"TaxonomyFieldType","TermSetId":"set1","AnchorId":"00000000-0000-0000-0000-000000000000","Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Link","Title":"Link","TypeAsString":"URL","Hidden":false,"ReadOnlyField":false},
            {"InternalName":"S17Hidden","Title":"Hidden","TypeAsString":"Text","Hidden":true,"ReadOnlyField":false},
            {"InternalName":"Modified","Title":"Modified","TypeAsString":"DateTime","Hidden":false,"ReadOnlyField":true}
            ]}
            """;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = Uri.UnescapeDataString(request.RequestUri!.ToString());
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.Method.Method, url, body, request.Headers.TryGetValues("If-Match", out var values) ? values.Single() : null));
            if (request.Method.Method == "PATCH")
            {
                return PatchStatus is { } failed ? Answer(failed, """{"error":{"code":"x","message":"refused here"}}""") : Json("{}");
            }
            if (url.Contains("ValidateUpdateListItem", StringComparison.Ordinal))
            {
                return Json(ValidateAnswer);
            }
            if (url.Contains("clientPeoplePickerSearchUser", StringComparison.Ordinal))
            {
                var hits = JsonSerializer.Serialize("""[{"Key":"i:0#.f|membership|ann@contoso.com","DisplayText":"Ann","EntityData":{"Email":"ann@contoso.com"}},{"Key":"c:0o.c|federateddirectoryclaimprovider|g1","DisplayText":"Sales","EntityData":{}}]""");
                return Json($$"""{"value":{{hits}}}""");
            }
            if (url.Contains("/termStore/", StringComparison.Ordinal))
            {
                var fresh = request.Headers.Authorization?.Parameter == "fresh";
                return TermStatus is { } refused && !(fresh && FreshTokenAllowed) ? Answer(refused, """{"error":{"code":"accessDenied","message":"no term store"}}""")
                    : Json("""{"value":[{"id":"g2","labels":[{"name":"Beta","languageTag":"en-US","isDefault":true},{"name":"Bèta","languageTag":"nl-NL","isDefault":false}]},{"id":"g1","labels":[{"name":"Alpha","languageTag":"en-US","isDefault":true}]}]}""");
            }
            if (url.Contains("/versions", StringComparison.Ordinal))
            {
                return Json("""{"value":[{"id":"1.0"},{"id":"2.0"}]}""");
            }
            if (url.Contains("/listItem?", StringComparison.Ordinal))
            {
                return Json($$"""{"id":"7","eTag":{{JsonSerializer.Serialize(ETag)}},"contentType":{"id":"0x0101AA","name":"Document"},"fields":{{Fields}}}""");
            }
            if (url.Contains("/sites/contoso.sharepoint.com,s1,w1?", StringComparison.Ordinal))
            {
                return Json($$"""{"webUrl":"{{Web}}"}""");
            }
            if (url.Contains("/contenttypes('", StringComparison.Ordinal))
            {
                return Json(Definitions);
            }
            if (url.Contains("/lists(guid'l1')?", StringComparison.Ordinal))
            {
                return Json("""{"Title":"Documents","ContentTypesEnabled":true,"ContentTypes":[{"Name":"Document","StringId":"0x0101AA","Hidden":false},{"Name":"Contract","StringId":"0x0101BB","Hidden":false},{"Name":"Folder","StringId":"0x0120CC","Hidden":false},{"Name":"Secret","StringId":"0x0101DD","Hidden":true}]}""");
            }
            if (url.Contains("/items(7)?$select=", StringComparison.Ordinal))
            {
                return Json(People);
            }
            if (url.Contains("/FieldValuesAsText", StringComparison.Ordinal))
            {
                return Json("""{"S17Link":"https://contoso.com, Contoso"}""");
            }
            if (url.Contains("/drives/d1/items/i1?", StringComparison.Ordinal))
            {
                return Json("""{"id":"i1","name":"a.docx","size":2048,"file":{},"createdDateTime":"2026-11-01T09:00:00Z","createdBy":{"user":{"displayName":"Ann"}},"lastModifiedDateTime":"2026-11-06T10:00:00Z","lastModifiedBy":{"user":{"displayName":"Bob"}},"webUrl":"https://contoso.sharepoint.com/sites/a/Documents/a.docx","sharepointIds":{"siteUrl":"https://contoso.sharepoint.com/sites/a","siteId":"s1","webId":"w1","listId":"l1","listItemId":"7"},"publication":{"level":"checkout","checkedOutBy":{"user":{"displayName":"Carol"}}}}""");
            }
            return Answer(HttpStatusCode.NotFound, """{"error":{"code":"itemNotFound","message":"nope"}}""");
        }

        static HttpResponseMessage Json(string json) => Answer(HttpStatusCode.OK, json);

        static HttpResponseMessage Answer(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    internal static ColumnsApi Api(Server server, Tokens? tokens = null) =>
        new(new M365Client(new HttpClient(server), tokens ?? new Tokens()) { Delay = (_, _) => Task.CompletedTask, TenantRootHost = "contoso.sharepoint.com" });

    [Fact]
    public async Task Details_columns_and_values_are_read()
    {
        var server = new Server();

        var item = await Api(server).GetAsync("d1", "i1", default);

        Assert.Equal(("a.docx", 2048L, "Ann", "Bob", 2, "Carol", "Keep 7 years", "Documents"),
            (item.Details.Name, item.Details.Size, item.Details.CreatedBy, item.Details.ModifiedBy, item.Details.Versions, item.Details.CheckedOutTo, item.Details.RetentionLabel, item.Details.Library));
        Assert.Equal(Server.Web, item.WebUrl);
        Assert.Equal("contoso.sharepoint.com,s1,w1", item.Site);
        Assert.Equal(["Document", "Contract"], item.ContentTypes.Select(t => t.Name)); // not the folder's, not hidden ones
        Assert.Equal(["Title", "S17Text", "S17Number", "S17Date", "S17YesNo", "S17Choice", "S17Choices", "S17Person", "S17Term", "S17Link"], item.Columns.Select(c => c.Name));
        Assert.Equal("old", item.ValueOf("S17Text").Text);
        Assert.Equal(5.0, item.ValueOf("S17Number").Number);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 10, 0, 0, TimeSpan.Zero), item.ValueOf("S17Date").Date);
        Assert.True(item.ValueOf("S17YesNo").Boolean);
        Assert.Equal(["One"], item.ValueOf("S17Choice").Choices);
        Assert.Equal(new Person("i:0#.f|membership|bob@contoso.com", "Bob"), Assert.Single(item.ValueOf("S17Person").People));
        Assert.Equal(new Term("Alpha", "g1"), Assert.Single(item.ValueOf("S17Term").Terms));
        Assert.Equal("https://contoso.com, Contoso", item.ValueOf("S17Link").Display);
        Assert.Equal("\"e1,1\"", item.ETag);
    }

    [Fact]
    public void Field_definitions_become_columns()
    {
        using var definitions = JsonDocument.Parse(Server.Definitions);
        var columns = definitions.RootElement.GetProperty("value").EnumerateArray().Select(ColumnsApi.ParseColumn).ToDictionary(c => c.Name);

        Assert.Equal((ColumnKind.Text, 50), (columns["S17Text"].Kind, columns["S17Text"].MaxLength));
        Assert.Equal((ColumnKind.Number, null, 10.0), (columns["S17Number"].Kind, columns["S17Number"].Minimum, columns["S17Number"].Maximum));
        Assert.True(columns["S17Date"].DateOnly);
        Assert.Equal((ColumnKind.Choice, false), (columns["S17Choice"].Kind, columns["S17Choice"].Multi));
        Assert.Equal((ColumnKind.Choice, true), (columns["S17Choices"].Kind, columns["S17Choices"].Multi));
        Assert.Equal((ColumnKind.User, true), (columns["S17Person"].Kind, columns["S17Person"].Required));
        Assert.Equal((ColumnKind.Taxonomy, "set1", null), (columns["S17Term"].Kind, columns["S17Term"].TermSetId, columns["S17Term"].AnchorId));
        Assert.Equal(ColumnKind.ReadOnly, columns["S17Link"].Kind);
    }

    static Column C(ColumnKind kind, bool multi = false) => new("X", "X", kind) { Multi = multi };

    [Theory]
    [InlineData(ColumnKind.Number, false, """{"X":6.5}""")]
    [InlineData(ColumnKind.Choice, true, """{"X@odata.type":"Collection(Edm.String)","X":["One","Two"]}""")]
    [InlineData(ColumnKind.Choice, false, """{"X":"One"}""")]
    [InlineData(ColumnKind.Boolean, false, """{"X":true}""")]
    [InlineData(ColumnKind.Text, false, """{"X":"a b"}""")]
    public void Typed_values_go_to_Graph_without_the_site_s_locale(ColumnKind kind, bool multi, string expected)
    {
        var value = new ColumnValue { Number = 6.5, Choices = multi ? ["One", "Two"] : ["One"], Boolean = true, Text = "a b" };

        Assert.Equal(expected, ColumnsApi.GraphBody([(C(kind, multi), value)]));
    }

    [Fact]
    public void A_date_goes_in_UTC_and_empty_values_as_null()
    {
        var date = new ColumnValue { Date = new DateTimeOffset(2026, 11, 11, 9, 30, 0, TimeSpan.FromHours(1)) };

        Assert.Equal("""{"X":"2026-11-11T08:30:00Z"}""", ColumnsApi.GraphBody([(C(ColumnKind.DateTime), date)]));
        Assert.Equal("""{"X":null}""", ColumnsApi.GraphBody([(C(ColumnKind.Number), ColumnValue.Empty)]));
        Assert.Equal("""{"X":null}""", ColumnsApi.GraphBody([(C(ColumnKind.Text), ColumnValue.Empty)]));
        Assert.Equal("""{"X@odata.type":"Collection(Edm.String)","X":[]}""", ColumnsApi.GraphBody([(C(ColumnKind.Choice, true), ColumnValue.Empty)]));
    }

    [Fact]
    public void People_and_terms_go_to_REST_in_its_form_format()
    {
        var people = new ColumnValue { People = [new Person("i:0#.f|membership|ann@contoso.com", "Ann")] };
        var terms = new ColumnValue { Terms = [new Term("Alpha", "g1"), new Term("Be;ta|x", "g2")] };

        Assert.Equal("""[{"Key":"i:0#.f|membership|ann@contoso.com"}]""", ColumnsApi.FormValue(C(ColumnKind.User), people));
        Assert.Equal("Alpha|g1;Be ta x|g2;", ColumnsApi.FormValue(C(ColumnKind.Taxonomy, true), terms));
        Assert.Equal("", ColumnsApi.FormValue(C(ColumnKind.User), ColumnValue.Empty));
        Assert.Equal("", ColumnsApi.FormValue(C(ColumnKind.Taxonomy), ColumnValue.Empty));
    }

    [Fact]
    public async Task A_save_sends_typed_values_to_Graph_with_If_Match_and_the_rest_to_REST()
    {
        var server = new Server();
        var api = Api(server);
        var item = await api.GetAsync("d1", "i1", default);
        server.Requests.Clear();

        var problems = await api.SaveAsync(item, item.ContentTypeId,
            [(item.Columns.Single(c => c.Name == "S17Text"), new ColumnValue { Text = "new" }), (item.Columns.Single(c => c.Name == "S17Term"), new ColumnValue { Terms = [new Term("Beta", "g2")] })], default);

        Assert.True(problems.None);
        var patch = Assert.Single(server.Requests, r => r.Method == "PATCH");
        Assert.EndsWith("/drives/d1/items/i1/listItem/fields", patch.Url);
        Assert.Equal("""{"S17Text":"new"}""", patch.Body);
        Assert.Equal("\"e1,1\"", patch.IfMatch);
        var validate = Assert.Single(server.Requests, r => r.Url.Contains("ValidateUpdateListItem", StringComparison.Ordinal));
        Assert.Contains("Beta|g2;", validate.Body);
        Assert.Contains("\"bNewDocumentUpdate\":true", validate.Body); // the PATCH made the version
    }

    [Fact]
    public async Task People_alone_check_the_eTag_first_and_make_a_version()
    {
        var server = new Server();
        var api = Api(server);
        var item = await api.GetAsync("d1", "i1", default);
        server.ETag = "\"e1,2\""; // changed elsewhere

        var problems = await api.SaveAsync(item, item.ContentTypeId, [(item.Columns.Single(c => c.Name == "S17Person"), new ColumnValue { People = [new Person("k", "Ann")] })], default);

        Assert.Equal("changed", problems.Item);
        Assert.DoesNotContain(server.Requests, r => r.Url.Contains("ValidateUpdateListItem", StringComparison.Ordinal));

        server.ETag = item.ETag!;
        server.Requests.Clear();
        await api.SaveAsync(item, item.ContentTypeId, [(item.Columns.Single(c => c.Name == "S17Person"), new ColumnValue { People = [new Person("k", "Ann")] })], default);
        Assert.Contains("\"bNewDocumentUpdate\":false", Assert.Single(server.Requests, r => r.Url.Contains("ValidateUpdateListItem", StringComparison.Ordinal)).Body);
    }

    [Fact]
    public async Task A_change_elsewhere_and_refused_columns_are_said()
    {
        var server = new Server { PatchStatus = HttpStatusCode.PreconditionFailed };
        var api = Api(server);
        var item = await api.GetAsync("d1", "i1", default);
        var text = (item.Columns.Single(c => c.Name == "S17Text"), new ColumnValue { Text = "new" });

        Assert.Equal("changed", (await api.SaveAsync(item, item.ContentTypeId, [text], default)).Item);

        server.PatchStatus = HttpStatusCode.Forbidden;
        Assert.Equal("refused here", (await api.SaveAsync(item, item.ContentTypeId, [text], default)).Item);

        server.PatchStatus = null;
        server.ValidateAnswer = """{"value":[{"FieldName":"S17Term","HasException":true,"ErrorMessage":"not a term"}]}""";
        var problems = await api.SaveAsync(item, item.ContentTypeId, [(item.Columns.Single(c => c.Name == "S17Term"), new ColumnValue { Terms = [new Term("x", "y")] })], default);
        Assert.Equal("not a term", problems.Columns["S17Term"]);
    }

    [Fact]
    public async Task A_partial_save_hands_back_the_item_s_new_eTag()
    {
        var server = new Server { ValidateAnswer = """{"value":[{"FieldName":"S17Term","HasException":true,"ErrorMessage":"not a term"}]}""" };
        var api = Api(server);
        var item = await api.GetAsync("d1", "i1", default);
        server.ETag = "\"e1,2\""; // what the text PATCH made it

        var problems = await api.SaveAsync(item, item.ContentTypeId,
            [(item.Columns.Single(c => c.Name == "S17Text"), new ColumnValue { Text = "new" }), (item.Columns.Single(c => c.Name == "S17Term"), new ColumnValue { Terms = [new Term("x", "y")] })], default);

        Assert.Equal("not a term", problems.Columns["S17Term"]);
        Assert.Equal("\"e1,2\"", problems.ETag);
        Assert.Null((await api.SaveAsync(item with { ETag = problems.ETag }, item.ContentTypeId, [], default)).Item);
    }

    [Fact]
    public void A_required_column_changed_only_in_the_browser_counts_as_missing_too()
    {
        var link = new Column("Link", "Link", ColumnKind.ReadOnly) { Required = true };
        var text = new Column("Text", "Text", ColumnKind.Text) { Required = true };
        var item = new ItemColumns("d1", "i1", "s", "w", "l", "7", new ItemDetails("a", false, 0, default, null, default, null))
        {
            ContentTypeId = "0x0101",
            Columns = [link, text],
            Values = new Dictionary<string, ColumnValue> { ["Text"] = new() { Text = "filled" }, ["Link"] = new() { Display = "" } },
        };

        Assert.Equal(["Link"], ColumnsApi.MissingRequired(item));
    }

    [Fact]
    public async Task A_new_content_type_goes_first()
    {
        var server = new Server();
        var api = Api(server);
        var item = await api.GetAsync("d1", "i1", default);
        server.Requests.Clear();

        await api.SaveAsync(item, "0x0101BB", [(item.Columns.Single(c => c.Name == "S17Text"), new ColumnValue { Text = "new" })], default);

        var patches = server.Requests.Where(r => r.Method == "PATCH").ToList();
        Assert.Equal(2, patches.Count);
        Assert.EndsWith("/listItem", patches[0].Url);
        Assert.Equal("""{"contentType":{"id":"0x0101BB"}}""", patches[0].Body);
        Assert.Equal("\"e1,1\"", patches[0].IfMatch);
        Assert.Null(patches[1].IfMatch); // the type change moved the eTag
    }

    [Fact]
    public async Task People_come_from_SharePoint_s_people_picker()
    {
        var server = new Server();
        var api = Api(server);
        var person = new Column("S17Person", "Person", ColumnKind.User) { SelectionMode = 0 };

        var found = await api.SearchPeopleAsync(Server.Web, person, "an", default);

        Assert.Equal(["Ann (ann@contoso.com)", "Sales"], found.Select(p => p.Name));
        Assert.Contains("\"PrincipalType\":1", server.Requests.Single().Body);
    }

    [Fact]
    public async Task Terms_come_sorted_in_the_user_s_language_and_a_403_says_the_term_store_is_not_allowed()
    {
        var server = new Server();
        var api = Api(server);
        var column = new Column("S17Term", "Term", ColumnKind.Taxonomy) { TermSetId = "set1" };

        Assert.Equal(["Alpha", "Bèta"], (await api.TermsAsync("contoso.sharepoint.com,s1,w1", column, "nl-NL", default)).Select(t => t.Label));

        server.TermStatus = HttpStatusCode.Forbidden;
        await Assert.ThrowsAsync<TermStoreUnavailableException>(() => api.TermsAsync("contoso.sharepoint.com,s1,w1", column, "nl-NL", default));
    }

    [Fact]
    public async Task A_403_renews_the_token_once_since_an_admin_may_have_consented_after_it_was_issued()
    {
        var tokens = new Tokens();
        var server = new Server { TermStatus = HttpStatusCode.Forbidden, FreshTokenAllowed = true };
        var api = Api(server, tokens);
        var column = new Column("S17Term", "Term", ColumnKind.Taxonomy) { TermSetId = "set1" };

        var terms = await api.TermsAsync("contoso.sharepoint.com,s1,w1", column, "en-US", default);

        Assert.Equal(["Alpha", "Beta"], terms.Select(t => t.Label));
        Assert.Contains("\"nbf\"", Assert.Single(tokens.Claims));
    }

    sealed class RefusingTokens : ITokenSource
    {
        public int Claims;

        public ValueTask<string> GetAccessTokenAsync(string resource, CancellationToken ct) => ValueTask.FromResult("token");

        public ValueTask<string> GetAccessTokenAsync(string resource, string claims, CancellationToken ct)
        {
            Claims++;
            return ValueTask.FromResult("token");
        }

        public ValueTask<string> RenewAsync(string resource, string claims, CancellationToken ct) => throw new InvalidOperationException("interaction required");
    }

    [Fact]
    public async Task A_renewal_that_is_refused_keeps_the_token_in_hand_and_the_picker_says_the_term_store_isn_t_allowed()
    {
        var tokens = new RefusingTokens();
        var server = new Server { TermStatus = HttpStatusCode.Forbidden };
        var api = new ColumnsApi(new M365Client(new HttpClient(server), tokens) { Delay = (_, _) => Task.CompletedTask, TenantRootHost = "contoso.sharepoint.com" });
        var column = new Column("S17Term", "Term", ColumnKind.Taxonomy) { TermSetId = "set1" };

        await Assert.ThrowsAsync<TermStoreUnavailableException>(() => api.TermsAsync("contoso.sharepoint.com,s1,w1", column, "en-US", default));

        Assert.Equal(0, tokens.Claims); // never through the claims-challenge path, which drops the cached token
        Assert.Equal("a.docx", (await api.GetAsync("d1", "i1", default)).Details.Name); // other calls go on with the token in hand
    }

    [Fact]
    public async Task A_token_is_renewed_once_per_quarter_hour_at_most()
    {
        var tokens = new Tokens();
        var now = new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        var client = new M365Client(new HttpClient(new Server()), tokens) { Clock = () => now };

        Assert.True(await client.RenewTokenAsync(M365Client.GraphResource, default));
        Assert.False(await client.RenewTokenAsync(M365Client.GraphResource, default));
        Assert.True(await client.RenewTokenAsync("https://contoso.sharepoint.com", default));
        now += M365Client.RenewEvery;
        Assert.True(await client.RenewTokenAsync(M365Client.GraphResource, default));
        Assert.Equal(3, tokens.Claims.Count);
    }

    [Fact]
    public async Task An_item_without_a_list_item_says_so()
    {
        await Assert.ThrowsAsync<NoListItemException>(() => Api(new Server()).GetAsync("d1", "missing", default));
    }

    [Theory]
    [InlineData(ColumnKind.Text, "", true)]
    [InlineData(ColumnKind.Text, " ", true)]
    [InlineData(ColumnKind.Text, "a", false)]
    public void Whitespace_is_empty(ColumnKind kind, string text, bool empty)
    {
        Assert.Equal(empty, new ColumnValue { Text = text }.IsEmpty(kind));
    }
}
