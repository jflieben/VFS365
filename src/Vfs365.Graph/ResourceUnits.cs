namespace Vfs365.Graph;

/// <summary>Graph calls against SharePoint, grouped by how SharePoint prices them.</summary>
public enum GraphOperation
{
    GetItem,
    Download,
    DeltaWithToken,
    DeltaWithoutToken,
    ListChildren,
    Create,
    Update,
    Delete,
    Upload,
    Permissions,
}

/// <summary>Per-app, per-tenant budget. Shared by every user of VFS365 in that tenant.</summary>
public readonly record struct AppBudget(int PerMinute, int Per24Hours);

/// <summary>
/// SharePoint resource unit (RU) costs and throttling budgets, from "Avoid getting throttled or blocked in SharePoint Online" (2026-08).
/// Microsoft may change these. Fragment PUTs to an upload URL and ranged GETs to a downloadUrl are assumed unpriced (unverified).
/// </summary>
public static class ResourceUnits
{
    public const int UserRequestsPer5Minutes = 3_000;

    public static int Cost(GraphOperation operation) => operation switch
    {
        GraphOperation.GetItem or GraphOperation.Download or GraphOperation.DeltaWithToken => 1,
        GraphOperation.Permissions => 5,
        _ => 2,
    };

    /// <summary>
    /// The cost of a request from its method and URL. SharePoint REST calls (discovery) have no published price and count as 2;
    /// pre-authenticated transfer URLs count as 0.
    /// </summary>
    public static int Estimate(HttpMethod method, Uri uri, bool authenticated)
    {
        if (!authenticated)
        {
            return 0;
        }
        if (method != HttpMethod.Get)
        {
            return Cost(GraphOperation.Update);
        }
        var path = uri.AbsolutePath;
        if (path.Contains("/_api/", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/children", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/versions", StringComparison.OrdinalIgnoreCase))
        {
            return Cost(GraphOperation.ListChildren);
        }
        if (path.EndsWith("/delta", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/delta()", StringComparison.OrdinalIgnoreCase))
        {
            return Cost(uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase) ? GraphOperation.DeltaWithToken : GraphOperation.DeltaWithoutToken);
        }
        return Cost(GraphOperation.GetItem);
    }

    public static AppBudget AppBudgetFor(int licenses) => licenses switch
    {
        <= 1_000 => new(1_250, 1_200_000),
        <= 5_000 => new(2_500, 2_400_000),
        <= 15_000 => new(3_750, 3_600_000),
        <= 50_000 => new(5_000, 4_800_000),
        _ => new(6_250, 6_000_000),
    };

    /// <summary>Tenant-wide limit across all apps, per 5 minutes.</summary>
    public static int TenantPer5MinutesFor(int licenses) => licenses switch
    {
        <= 1_000 => 18_750,
        <= 5_000 => 37_500,
        <= 15_000 => 56_250,
        <= 50_000 => 75_000,
        _ => 93_750,
    };
}
