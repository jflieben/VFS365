namespace Vfs365.Graph;

/// <summary>
/// Where tokens may go: https on the default port to Microsoft Graph or to the tenant's own SharePoint hosts (root, OneDrive and
/// multi-geo locations: contoso, contoso-my, contosoEUR, contosoEUR-my, from any of them). M365Client applies it to every request.
/// </summary>
public static class TrustedHosts
{
    const string Graph = "graph.microsoft.com";

    /// <param name="tenantRootHost">The tenant's SharePoint root host, for example contoso.sharepoint.com (contoso-my also works).</param>
    public static bool IsTrusted(Uri uri, string tenantRootHost)
    {
        if (!uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length > 0)
        {
            return false;
        }
        var host = uri.IdnHost.TrimEnd('.');
        if (host.Equals(Graph, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var dot = tenantRootHost.IndexOf('.');
        if (dot <= 0 || host.Length <= tenantRootHost.Length - dot)
        {
            return false;
        }
        var domain = tenantRootHost[dot..]; // .sharepoint.com
        var tenant = tenantRootHost[..dot];
        if (tenant.EndsWith("-my", StringComparison.OrdinalIgnoreCase))
        {
            tenant = tenant[..^3];
        }
        if (tenant.Length == 0 || !host.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var label = host[..^domain.Length];
        if (label.EndsWith("-my", StringComparison.OrdinalIgnoreCase))
        {
            label = label[..^3];
        }
        // Multi-geo: central and location hosts (contoso, contosoEUR) trust each other; two locations only with known codes, since
        // without one, contoso and contxyz would look like the same name with two codes
        return label.Equals(tenant, StringComparison.OrdinalIgnoreCase) || PlusGeo(label, tenant) || PlusGeo(tenant, label)
            || (label.Length == tenant.Length && label.Length > 3 && label[..^3].Equals(tenant[..^3], StringComparison.OrdinalIgnoreCase)
                && Geos.Contains(label[^3..]) && Geos.Contains(tenant[^3..]));
    }

    static bool PlusGeo(string longer, string shorter) =>
        longer.Length == shorter.Length + 3 && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase) && longer[^3..].All(char.IsAsciiLetter);

    /// <summary>Microsoft 365 Multi-Geo location codes.</summary>
    static readonly HashSet<string> Geos = new(StringComparer.OrdinalIgnoreCase)
    {
        "APC", "ARE", "AUS", "BRA", "CAN", "CHE", "DEU", "ESP", "EUR", "FRA", "GBR", "IND", "ISR", "ITA", "JPN", "KOR", "MEX", "NAM", "NOR",
        "NZL", "POL", "QAT", "SWE", "TWN", "ZAF",
    };
}
