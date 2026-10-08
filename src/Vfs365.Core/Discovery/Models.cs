namespace Vfs365.Core.Discovery;

/// <summary>A document library as returned by SharePoint Search.</summary>
public sealed record DiscoveredLibrary(string SiteId, string WebId, string ListId, string WebUrl, string SiteCollectionUrl, string SiteTitle, string ListTitle)
{
    public string Key => LibraryKey.Of(SiteId, WebId, ListId);
}

public sealed record SearchPage(IReadOnlyList<DiscoveredLibrary> Libraries, int RowCount);

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

public sealed record PinnedLibrary(string ListId, ListMetadata List);

/// <summary>A pinned URL resolved: the web it belongs to and its document libraries (all of them for a site URL, one for a library URL).</summary>
public sealed record PinnedLocation(string WebUrl, string SiteId, string WebId, string SiteTitle, SiteMetadata Site, IReadOnlyList<PinnedLibrary> Libraries);

/// <summary>A library shown to the user. CheckedAt: when its site and library metadata was read (null in state of earlier versions).</summary>
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
    DateTimeOffset? CheckedAt = null);

public sealed record SkippedLibrary(string Key, string WebUrl, string Title, string Reason, string? InternalName = null)
{
    public static SkippedLibrary For(DiscoveredLibrary library, string reason, string? internalName = null) =>
        new(library.Key, library.WebUrl, library.ListTitle, reason, internalName);
}

/// <summary>
/// Kept between runs: the visible set (for the hiding safety check and to mount before discovery runs), libraries that never need
/// another lookup, and the oldest time a shown library's metadata was read (the fallback for entries without CheckedAt).
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
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Pinned = null);

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
