namespace Vfs365.Core.Discovery;

/// <summary>
/// A document library as returned by SharePoint Search. SiteTemplate: its site collection's template (GROUP, SITEPAGEPUBLISHING, ...), when
/// search reports it; subsites have their site collection's.
/// </summary>
public sealed record DiscoveredLibrary(string SiteId, string WebId, string ListId, string WebUrl, string SiteCollectionUrl, string SiteTitle, string ListTitle,
    string? SiteTemplate = null)
{
    public string Key => LibraryKey.Of(SiteId, WebId, ListId);
}

/// <summary>LastDocId: the DocId of the page's last row, which the next page starts after; null when search didn't return it.</summary>
public sealed record SearchPage(IReadOnlyList<DiscoveredLibrary> Libraries, int RowCount, long? LastDocId = null);

public sealed record MyDrive(string DriveId, string WebUrl);

public sealed record SiteMetadata(bool ReadOnly, bool WriteLocked);

public sealed record ListMetadata(
    string Title,
    bool Hidden,
    int? BaseTemplate,
    bool IsCatalog,
    bool IsSystemList,
    string? TemplateFeatureId,
    bool ForceCheckout,
    bool ExcludeFromOfflineClient,
    long ItemCount,
    string? InternalName = null);

/// <summary>A document library of a web with its metadata, as listed by the web (pinned locations, discovery per site).</summary>
public sealed record PinnedLibrary(string ListId, ListMetadata List);

/// <summary>A pinned URL resolved: the web it belongs to and its document libraries (all of them for a site URL, one for a library URL).</summary>
public sealed record PinnedLocation(string WebUrl, string SiteId, string WebId, string SiteTitle, SiteMetadata Site, IReadOnlyList<PinnedLibrary> Libraries);

/// <summary>
/// A library shown to the user. CheckedAt: when its site and library metadata was read (null in state of earlier versions). SiteTemplate:
/// its site collection's template as search reports it.
/// </summary>
public sealed record LibraryEntry(
    string Key,
    string SiteId,
    string WebId,
    string ListId,
    string WebUrl,
    string SiteTitle,
    string LibraryTitle,
    bool ReadOnly,
    long ItemCount,
    string? DriveId = null,
    string? InternalName = null,
    DateTimeOffset? CheckedAt = null,
    string? SiteTemplate = null);

public sealed record SkippedLibrary(string Key, string WebUrl, string Title, string Reason, string? InternalName = null)
{
    public static SkippedLibrary For(DiscoveredLibrary library, string reason, string? internalName = null) =>
        new(library.Key, library.WebUrl, library.ListTitle, reason, internalName);
}

/// <summary>A library or pinned URL left out for a reason that can change, and when that was checked; reused like library metadata.</summary>
public sealed record CheckedExclusion(string Reason, DateTimeOffset CheckedAt);

/// <summary>
/// Kept between runs: the visible set (for the hiding safety check and to mount before discovery runs), libraries that never need
/// another lookup, and the oldest time a shown library's metadata was read (the fallback for entries without CheckedAt).
/// TenantId: the signed-in tenant, when the caller passed it. Misses: shown libraries search didn't return, with the number of runs in
/// a row (hidden at the second). Exclusions: libraries left out for a reason that can change (hidden, offline clients excluded).
/// EmptyPins: pinned URLs where nothing was found or nothing is shown.
/// </summary>
public sealed record DiscoveryState(
    DateTimeOffset RunAt,
    string TenantRoot,
    IReadOnlyList<LibraryEntry> Libraries,
    IReadOnlyDictionary<string, string> StaticExclusions,
    int RulesVersion = 0,
    MyDrive? OneDrive = null,
    DateTimeOffset MetadataAt = default,
    string? TenantHint = null,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Pinned = null,
    string? TenantId = null,
    IReadOnlyDictionary<string, int>? Misses = null,
    IReadOnlyDictionary<string, CheckedExclusion>? Exclusions = null,
    IReadOnlyDictionary<string, CheckedExclusion>? EmptyPins = null);

public sealed record DiscoveryResult(
    MyDrive OneDrive,
    string TenantRoot,
    IReadOnlyList<LibraryEntry> Libraries,
    IReadOnlyList<SkippedLibrary> Skipped,
    IReadOnlyList<SkippedLibrary> Errors,
    int SearchHits,
    string? SearchError,
    string? HidingSkippedReason,
    DiscoveryState State);

public static class LibraryKey
{
    public static string Of(string siteId, string webId, string listId) => $"{Normalize(siteId)}|{Normalize(webId)}|{Normalize(listId)}";

    public static string Normalize(string id) =>
        Guid.TryParse(id, out var guid) ? guid.ToString("D") : id.Trim().Trim('{', '}').ToLowerInvariant();
}
