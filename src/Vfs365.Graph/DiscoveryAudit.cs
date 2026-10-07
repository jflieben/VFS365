using System.Text.Json;
using Vfs365.Core.Discovery;

namespace Vfs365.Graph;

/// <summary>A document library the user can open that discovery didn't show, with the likely reason when one is visible.</summary>
public sealed record AuditMiss(string WebUrl, string Library, string Reason);

/// <summary>
/// Recall of a discovery run: sites the user reaches by other routes, each site's document libraries listed directly, and
/// the ones discovery missed. Read-only.
/// </summary>
public sealed record AuditReport(
    int SitesFromSearch,
    int SitesFollowed,
    int Hubs,
    int SitesChecked,
    int SitesNotListable,
    int LibrariesExpected,
    int LibrariesShown,
    IReadOnlyList<AuditMiss> Missed);

public sealed class DiscoveryAudit(M365Client client)
{
    const string ListSelect = "Id,Title,Hidden,BaseTemplate,IsCatalog,IsSystemList,TemplateFeatureId,ForceCheckout,ExcludeFromOfflineClient,ItemCount,EntityTypeName,NoCrawl";

    public async Task<AuditReport> RunAsync(DiscoveryResult result, DiscoveryOptions options, CancellationToken ct)
    {
        var fromSearch = await SearchSitesAsync(result.TenantRoot, ct);
        var followed = await FollowedSitesAsync(ct);
        var hubs = await HubSitesAsync(result.TenantRoot, ct);

        bool InScope(string url) => Wildcard.MatchesAny(url, options.IncludedSites) && !Wildcard.MatchesAny(url, options.ExcludedSites);
        var webs = fromSearch.Concat(followed).Concat(hubs).Concat(result.Libraries.Select(l => l.WebUrl))
            .Select(url => url.TrimEnd('/'))
            .Where(InScope)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var shown = result.Libraries.Select(l => (l.WebUrl.TrimEnd('/').ToLowerInvariant(), LibraryKey.Normalize(l.ListId))).ToHashSet();

        var missed = new List<AuditMiss>();
        var expected = 0;
        var notListable = 0;
        foreach (var web in webs)
        {
            JsonDocument? lists;
            try
            {
                lists = await client.GetJsonAsync(new Uri($"{web}/_api/web/lists?$select={ListSelect}&$filter=BaseTemplate eq 101"), ct, notFoundAsNull: true);
            }
            catch (M365RequestException e) when (e.Error == Core.RemoteError.AccessDenied)
            {
                notListable++; // access to some libraries but not the site: only search can find those
                continue;
            }
            if (lists is null)
            {
                notListable++;
                continue;
            }
            using (lists)
            {
                foreach (var list in lists.RootElement.GetProperty("value").EnumerateArray())
                {
                    var metadata = SharePointDiscoveryApi.ParseList(list);
                    if (!LibraryRules.Evaluate(metadata).Include)
                    {
                        continue;
                    }
                    expected++;
                    var id = list.TryGetProperty("Id", out var value) ? LibraryKey.Normalize(value.GetString() ?? "") : "";
                    if (!shown.Contains((web.ToLowerInvariant(), id)))
                    {
                        var noCrawl = list.TryGetProperty("NoCrawl", out var flag) && flag.ValueKind == JsonValueKind.True;
                        missed.Add(new AuditMiss(web, metadata.Title, noCrawl ? "library is excluded from search (NoCrawl)" : "not in search results (new, or the site is excluded from search)"));
                    }
                }
            }
        }
        return new AuditReport(fromSearch.Count, followed.Count, hubs.Count, webs.Count, notListable, expected, expected - missed.Count, missed);
    }

    async Task<List<string>> SearchSitesAsync(string tenantRoot, CancellationToken ct)
    {
        var urls = new List<string>();
        for (var startRow = 0; ; startRow += 500)
        {
            var query = Uri.EscapeDataString("contentclass:STS_Site OR contentclass:STS_Web");
            using var doc = (await client.GetJsonAsync(new Uri($"{tenantRoot}/_api/search/query?querytext='{query}'&trimduplicates=false&rowlimit=500&startrow={startRow}&selectproperties='Path'"), ct))!;
            var rows = doc.RootElement.GetProperty("PrimaryQueryResult").GetProperty("RelevantResults").GetProperty("Table").GetProperty("Rows");
            foreach (var row in rows.EnumerateArray())
            {
                foreach (var cell in row.GetProperty("Cells").EnumerateArray())
                {
                    if (cell.GetProperty("Key").GetString() == "Path" && cell.GetProperty("Value").GetString() is { Length: > 0 } path)
                    {
                        urls.Add(path);
                    }
                }
            }
            if (rows.GetArrayLength() < 500)
            {
                return urls;
            }
        }
    }

    async Task<List<string>> FollowedSitesAsync(CancellationToken ct)
    {
        using var doc = (await client.GetJsonAsync(new Uri($"{M365Client.GraphResource}/v1.0/me/followedSites?$select=webUrl"), ct))!;
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(s => s.GetProperty("webUrl").GetString()).OfType<string>().ToList();
    }

    async Task<List<string>> HubSitesAsync(string tenantRoot, CancellationToken ct)
    {
        try
        {
            using var doc = await client.GetJsonAsync(new Uri($"{tenantRoot}/_api/SP.HubSites?$select=SiteUrl"), ct, notFoundAsNull: true);
            return doc is null ? [] : doc.RootElement.GetProperty("value").EnumerateArray().Select(h => h.GetProperty("SiteUrl").GetString()).OfType<string>().ToList();
        }
        catch (M365RequestException)
        {
            return [];
        }
    }
}
