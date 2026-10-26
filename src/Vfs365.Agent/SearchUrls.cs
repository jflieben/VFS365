using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>What a search on the drive covers.</summary>
public enum SearchArea { Everything, OneDrive, Site, Library, Folder }

/// <summary>
/// Where a search looks. TenantRoot: https://contoso.sharepoint.com. SiteUrl: the site of a site, library or folder. ScopeUrl: the web
/// address of the library's top or the folder (Graph's webUrl); LibraryUrl: its library's top.
/// </summary>
public sealed record SearchScope(SearchArea Area, string TenantRoot, string? SiteUrl = null, string? ScopeUrl = null, string? LibraryUrl = null);

/// <summary>Microsoft 365 search pages for words in a scope. Only https addresses on the tenant's own SharePoint hosts come out.</summary>
public static class SearchUrls
{
    const int MaxUrlLength = 2000;

    /// <summary>
    /// The page that shows <paramref name="terms"/> in a scope: the tenant's SharePoint search (E1), limited by path: to the site, library,
    /// folder or OneDrive (P1). A personal site's own search page found nothing for a path: query that SharePoint's search API answered
    /// (0.7.2 test); the tenant's page searched fine. Null when the page isn't safe to open.
    /// </summary>
    public static string? For(SearchScope scope, string terms)
    {
        var root = scope.TenantRoot.TrimEnd('/');
        var within = scope.Area == SearchArea.Site ? scope.SiteUrl : scope.ScopeUrl;
        var query = scope.Area != SearchArea.Everything && within is not null && Kql(terms, within) is { } kql ? kql : terms;
        return Safe($"{root}/_layouts/15/search.aspx?q={Uri.EscapeDataString(query)}", root);
    }

    /// <summary>
    /// The candidate pages for spike 15 (unverified which honour the scope), each with an ID to name it in the results. Entries that
    /// fail <see cref="Safe"/> are left out.
    /// </summary>
    public static IReadOnlyList<(string Id, string Url)> Candidates(SearchScope scope, string terms)
    {
        var root = scope.TenantRoot.TrimEnd('/');
        var site = scope.SiteUrl?.TrimEnd('/');
        var list = new List<(string, string)>();
        void Add(string id, string? url)
        {
            if (Safe(url, root) is { } safe)
            {
                list.Add((id, safe));
            }
        }
        string Q(string text) => Uri.EscapeDataString(text);

        Add("E1 tenant search", $"{root}/_layouts/15/search.aspx?q={Q(terms)}");
        Add("E2 tenant files", $"{root}/_layouts/15/search.aspx/files?q={Q(terms)}");
        if (site is not null)
        {
            Add("S1 site all", $"{site}/_layouts/15/search.aspx/siteall?q={Q(terms)}");
            Add("S2 site files", $"{site}/_layouts/15/search.aspx/sitefiles?q={Q(terms)}");
        }
        var folder = scope.ScopeUrl is { } at && Uri.TryCreate(at, UriKind.Absolute, out var folderUri) ? Uri.UnescapeDataString(folderUri.AbsolutePath) : null;
        if (scope.ScopeUrl is { } scoped && Kql(terms, scoped) is { } kql)
        {
            Add("P1 tenant + path", $"{root}/_layouts/15/search.aspx?q={Q(kql)}");
            Add("P3 tenant files + path", $"{root}/_layouts/15/search.aspx/files?q={Q(kql)}");
            if (site is not null)
            {
                Add("P2 site + path", $"{site}/_layouts/15/search.aspx/siteall?q={Q(kql)}");
            }
        }
        var personal = site?.Contains("-my.", StringComparison.OrdinalIgnoreCase) == true;
        if (scope.LibraryUrl is { } library && !personal)
        {
            Add("L1 library view", $"{library.TrimEnd('/')}/Forms/AllItems.aspx?view=7&q={Q(terms)}");
            if (folder is not null)
            {
                Add("L2 library view + folder", $"{library.TrimEnd('/')}/Forms/AllItems.aspx?id={Q(folder)}&view=7&q={Q(terms)}");
            }
        }
        if (personal)
        {
            Add("O1 onedrive", $"{site}/_layouts/15/onedrive.aspx?view=7&q={Q(terms)}");
            if (folder is not null)
            {
                Add("O2 onedrive + folder", $"{site}/_layouts/15/onedrive.aspx?id={Q(folder)}&view=7&q={Q(terms)}");
            }
        }
        return list;
    }

    /// <summary>
    /// The words limited to everything below <paramref name="scopeUrl"/>: "(budget) path:"https://contoso.sharepoint.com/sites/x/Shared Documents"".
    /// Null when the address can't be quoted.
    /// </summary>
    public static string? Kql(string terms, string scopeUrl)
    {
        var path = Uri.UnescapeDataString(scopeUrl).TrimEnd('/');
        if (path.Contains('"'))
        {
            return null;
        }
        return terms.Length == 0 ? $"path:\"{path}\"" : $"({terms}) path:\"{path}\"";
    }

    /// <summary>The URL when it is https on one of the tenant's SharePoint hosts and short enough to open; null otherwise.</summary>
    public static string? Safe(string? url, string tenantRoot) =>
        url is { Length: <= MaxUrlLength } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && Uri.TryCreate(tenantRoot, UriKind.Absolute, out var rootUri)
            && rootUri.Host.Length > 0 && TrustedHosts.IsTrusted(uri, rootUri.Host) && !uri.Host.Equals("graph.microsoft.com", StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
}
