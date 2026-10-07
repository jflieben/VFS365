using System.Text.Json;
using System.Xml;
using Vfs365.Core.Discovery;

namespace Vfs365.Graph;

/// <summary>Discovery calls: Graph for the user's OneDrive and library drives, SharePoint REST for search and metadata.</summary>
public sealed class SharePointDiscoveryApi(M365Client client) : ISharePointDiscoveryApi
{
    const string SelectProperties = "Title,Path,ListId,SiteId,WebId,SPWebUrl,SPSiteUrl,SiteTitle";
    const string ListSelect = "Title,Hidden,BaseTemplate,IsCatalog,IsSystemList,TemplateFeatureId,ForceCheckout,ExcludeFromOfflineClient,ItemCount,EntityTypeName";

    public async Task<MyDrive> GetMyDriveAsync(CancellationToken ct)
    {
        using var doc = await RequiredAsync(new Uri($"{M365Client.GraphResource}/v1.0/me/drive?$select=id,webUrl"), ct);
        return new(Str(doc.RootElement, "id")!, Str(doc.RootElement, "webUrl")!);
    }

    public async Task<SearchPage> SearchDocumentLibrariesAsync(string tenantRoot, int startRow, int rowLimit, CancellationToken ct)
    {
        var query = Uri.EscapeDataString("contentclass:STS_List_DocumentLibrary");
        var select = Uri.EscapeDataString(SelectProperties);
        var uri = new Uri($"{tenantRoot}/_api/search/query?querytext='{query}'&trimduplicates=false&rowlimit={rowLimit}&startrow={startRow}&selectproperties='{select}'");
        using var doc = await RequiredAsync(uri, ct);
        return ParseSearchPage(doc.RootElement);
    }

    public async Task<SiteMetadata> GetSiteAsync(string webUrl, CancellationToken ct)
    {
        using var doc = await RequiredAsync(new Uri($"{webUrl}/_api/site?$select=ReadOnly,WriteLocked"), ct);
        return new(Bool(doc.RootElement, "ReadOnly"), Bool(doc.RootElement, "WriteLocked"));
    }

    public async Task<ListMetadata?> GetListAsync(string baseUrl, string listId, CancellationToken ct)
    {
        using var doc = await client.GetJsonAsync(new Uri($"{baseUrl.TrimEnd('/')}/_api/lists/GetById('{listId}')?$select={ListSelect}"), ct, notFoundAsNull: true);
        return doc is null ? null : ParseList(doc.RootElement);
    }

    public async Task<string> GetLibraryDriveIdAsync(LibraryEntry library, CancellationToken ct)
    {
        var host = new Uri(library.WebUrl).Host;
        var uri = new Uri($"{M365Client.GraphResource}/v1.0/sites/{host},{library.SiteId},{library.WebId}/lists/{library.ListId}/drive?$select=id");
        using var doc = await RequiredAsync(uri, ct);
        return Str(doc.RootElement, "id")!;
    }

    public async Task<PinnedLocation?> ResolveLocationAsync(string url, CancellationToken ct)
    {
        var target = new Uri(url.Trim());
        var host = target.GetLeftPart(UriPartial.Authority);
        var segments = Uri.UnescapeDataString(target.AbsolutePath).Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        // The web: the longest leading part of the path that answers as one (a folder or view link points below it)
        // never above the site collection of a /sites/x, /teams/x or /portals/x URL (a wrong link mustn't land on the root site)
        var minimum = segments.Length >= 2 && segments[0].ToLowerInvariant() is "sites" or "teams" or "portals" ? 2 : 0;
        JsonDocument? web = null;
        var webDepth = segments.Length;
        for (; webDepth >= minimum && web is null; webDepth--)
        {
            web = await ProbeAsync(new Uri($"{host}{PathOf(segments, webDepth)}/_api/web?$select=Id,Title,Url"), ct);
        }
        if (web is null)
        {
            return null;
        }
        webDepth++;
        using (web)
        {
            var webUrl = Str(web.RootElement, "Url")?.TrimEnd('/') ?? $"{host}{PathOf(segments, webDepth)}";
            var webId = Str(web.RootElement, "Id")!;
            var title = Str(web.RootElement, "Title") ?? webUrl[(webUrl.LastIndexOf('/') + 1)..];
            using var site = await RequiredAsync(new Uri($"{webUrl}/_api/site?$select=Id,ReadOnly,WriteLocked"), ct);
            var siteMetadata = new SiteMetadata(Bool(site.RootElement, "ReadOnly"), Bool(site.RootElement, "WriteLocked"));
            var siteId = Str(site.RootElement, "Id")!;
            var webSegments = new Uri(webUrl).AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Length;

            var libraries = new List<PinnedLibrary>();
            if (segments.Length <= webSegments)
            {
                using var lists = await RequiredAsync(new Uri($"{webUrl}/_api/web/lists?$select=Id,{ListSelect}&$filter=BaseTemplate eq 101"), ct);
                libraries.AddRange(lists.RootElement.GetProperty("value").EnumerateArray().Select(l => new PinnedLibrary(Str(l, "Id")!, ParseList(l))));
            }
            else
            {
                // The library: the longest part of the path below the web that is a list root
                for (var depth = segments.Length; depth > webSegments && libraries.Count == 0; depth--)
                {
                    var listPath = Uri.EscapeDataString(("/" + string.Join('/', segments.Take(depth))).Replace("'", "''"));
                    using var list = await ProbeAsync(new Uri($"{webUrl}/_api/web/GetList(@p)?@p='{listPath}'&$select=Id,{ListSelect}"), ct);
                    if (list is not null)
                    {
                        libraries.Add(new PinnedLibrary(Str(list.RootElement, "Id")!, ParseList(list.RootElement)));
                    }
                }
            }
            return new PinnedLocation(webUrl, siteId, webId, title, siteMetadata, libraries);
        }
    }

    /// <summary>"/a/b" from the first <paramref name="count"/> segments, escaped for a URL; "" for none.</summary>
    static string PathOf(string[] segments, int count) =>
        count <= 0 ? "" : "/" + string.Join('/', segments.Take(count).Select(Uri.EscapeDataString));

    /// <summary>Parses a search response (odata=nometadata). Rows without IDs are dropped; RowCount still counts them for paging.</summary>
    public static SearchPage ParseSearchPage(JsonElement root)
    {
        var rows = root.GetProperty("PrimaryQueryResult").GetProperty("RelevantResults").GetProperty("Table").GetProperty("Rows");
        var libraries = new List<DiscoveredLibrary>();
        foreach (var row in rows.EnumerateArray())
        {
            var cells = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in row.GetProperty("Cells").EnumerateArray())
            {
                if (Str(cell, "Key") is { } key && Str(cell, "Value") is { Length: > 0 } value)
                {
                    cells[key] = value;
                }
            }
            string? Get(string key) => cells.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

            var webUrl = (SharePointUrls.WebUrlFromListPath(Get("Path")) ?? Get("SPWebUrl") ?? Get("SPSiteUrl") ?? Get("Path"))?.TrimEnd('/');
            if (Get("ListId") is not { } listId || Get("SiteId") is not { } siteId || Get("WebId") is not { } webId || webUrl is null)
            {
                continue;
            }

            libraries.Add(new DiscoveredLibrary(
                LibraryKey.Normalize(siteId),
                LibraryKey.Normalize(webId),
                LibraryKey.Normalize(listId),
                webUrl,
                Get("SPSiteUrl")?.TrimEnd('/') ?? webUrl,
                Get("SiteTitle") ?? webUrl[(webUrl.LastIndexOf('/') + 1)..],
                Get("Title") ?? ""));
        }
        return new SearchPage(libraries, rows.GetArrayLength());
    }

    public static ListMetadata ParseList(JsonElement list) => new(
        Str(list, "Title") ?? "",
        Bool(list, "Hidden"),
        list.TryGetProperty("BaseTemplate", out var template) && template.ValueKind == JsonValueKind.Number ? template.GetInt32() : null,
        Bool(list, "IsCatalog"),
        Bool(list, "IsSystemList"),
        Str(list, "TemplateFeatureId"),
        Bool(list, "ForceCheckout"),
        Bool(list, "ExcludeFromOfflineClient"),
        list.TryGetProperty("ItemCount", out var count) && count.ValueKind == JsonValueKind.Number ? count.GetInt64() : 0,
        Str(list, "EntityTypeName") is { } entityTypeName ? XmlConvert.DecodeName(entityTypeName) : null);

    async Task<JsonDocument> RequiredAsync(Uri uri, CancellationToken ct) => (await client.GetJsonAsync(uri, ct))!;

    /// <summary>Null when the path isn't one: a 404, or the HTML page SharePoint returns for a path below a page (a view link).</summary>
    async Task<JsonDocument?> ProbeAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            return await client.GetJsonAsync(uri, ct, notFoundAsNull: true);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static bool Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
