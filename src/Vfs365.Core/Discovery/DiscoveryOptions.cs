namespace Vfs365.Core.Discovery;

public sealed record DiscoveryOptions
{
    /// <summary>Web URL patterns to show. Exclusions win.</summary>
    public IReadOnlyList<string> IncludedSites { get; init; } = ["*/sites/*", "*/teams/*"];

    /// <summary>M365AutoLink's defaults.</summary>
    public IReadOnlyList<string> ExcludedSites { get; init; } =
    [
        "*/groupforanswersinvivaengagedonotdelete*", "*/sites/Streamvideo*", "*/portals/personal/*", "*/sites/AllCompany*", "*/personal/*",
        "*/contentstorage/*", "*/sites/contentTypeHub*", "*/sites/pwa", "*/sites/AppCatalog*",
    ];

    /// <summary>Site or library URLs always shown (admin policy and user pins), also where search doesn't reach; site patterns don't apply.</summary>
    public IReadOnlyList<string> PinnedLocations { get; init; } = [];

    /// <summary>Hide nothing in a run whose visible set shrank by more than this fraction.</summary>
    public double HidingSafetyRatio { get; init; } = 0.40;

    public int MaxParallelSites { get; init; } = 4;

    /// <summary>A library's site and library metadata is reused this long after it was read; older and new ones are looked up.</summary>
    public TimeSpan MetadataMaxAge { get; init; } = TimeSpan.FromHours(48);

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}
