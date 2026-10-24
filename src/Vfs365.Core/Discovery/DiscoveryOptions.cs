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

    /// <summary>Site templates to show (GROUP, STS, SITEPAGEPUBLISHING, ...); empty: all. Exclusions win.</summary>
    public IReadOnlyList<string> IncludedSiteTemplates { get; init; } = [];

    public IReadOnlyList<string> ExcludedSiteTemplates { get; init; } = [];

    /// <summary>Site or library URLs always shown (admin policy and user pins), also where search doesn't reach; site patterns and templates don't apply.</summary>
    public IReadOnlyList<string> PinnedLocations { get; init; } = [];

    /// <summary>Hide nothing in a run whose visible set shrank by more than this fraction.</summary>
    public double HidingSafetyRatio { get; init; } = 0.40;

    public int MaxParallelSites { get; init; } = 4;

    /// <summary>
    /// A library's site and library metadata is reused this long after it was read; older and new ones are looked up. Read-only and
    /// locked sites, check-out and offline-client settings rarely change, and SharePoint enforces access anyway.
    /// </summary>
    public TimeSpan MetadataMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// A library read for the first time counts as read up to this much earlier (a fixed share per library key), so the libraries of
    /// a first run don't all expire at the same moment.
    /// </summary>
    public TimeSpan FirstReadJitter { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Per run, at most this share of the sites (at least <see cref="MinSiteRechecks"/>) has expired metadata read again, oldest
    /// first; the others keep theirs until a later run. New libraries and a refresh (MetadataMaxAge zero) are never held back.
    /// </summary>
    public double SiteRecheckShare { get; init; } = 0.5;

    public int MinSiteRechecks { get; init; } = 10;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>Why a site's template keeps its libraries hidden, or null. A template search didn't report is never held against a site.</summary>
    public string? TemplateRule(string? template) =>
        template is null ? null
        : ExcludedSiteTemplates.Any(pattern => TemplateMatches(template, pattern)) ? $"Site template {template} excluded"
        : IncludedSiteTemplates.Count > 0 && !IncludedSiteTemplates.Any(pattern => TemplateMatches(template, pattern)) ? $"Site template {template} not included"
        : null;

    /// <summary>Wildcards as for site patterns. A configuration number (GROUP#0) only counts when both sides have one.</summary>
    static bool TemplateMatches(string template, string pattern) =>
        template.Contains('#') && pattern.Contains('#') ? Wildcard.IsMatch(template, pattern.Trim()) : Wildcard.IsMatch(template.Split('#')[0], pattern.Trim().Split('#')[0]);
}
