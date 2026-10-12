namespace Vfs365.Core.Discovery;

/// <summary>Calls discovery needs. Implemented in Vfs365.Graph.</summary>
public interface ISharePointDiscoveryApi
{
    Task<MyDrive> GetMyDriveAsync(CancellationToken ct);

    /// <summary>
    /// One page of SharePoint Search for document libraries, security-trimmed to the user, in DocId order: rows with a DocId above
    /// <paramref name="afterDocId"/> (0: from the start), skipping the first <paramref name="startRow"/> of those.
    /// </summary>
    Task<SearchPage> SearchDocumentLibrariesAsync(string tenantRoot, long afterDocId, int startRow, int rowLimit, CancellationToken ct);

    Task<SiteMetadata> GetSiteAsync(string webUrl, CancellationToken ct);

    /// <summary>A web's document libraries with their metadata, in one call. Null when the user can't list them (access to some libraries only) or the web isn't found.</summary>
    Task<IReadOnlyList<PinnedLibrary>?> GetWebLibrariesAsync(string webUrl, CancellationToken ct);

    /// <summary>Null when the list isn't found under <paramref name="baseUrl"/>.</summary>
    Task<ListMetadata?> GetListAsync(string baseUrl, string listId, CancellationToken ct);

    Task<string> GetLibraryDriveIdAsync(LibraryEntry library, CancellationToken ct);

    /// <summary>
    /// A pinned site or library URL (also a folder or view link inside a library): its web and the document libraries it stands for.
    /// Null when nothing is found there.
    /// </summary>
    Task<PinnedLocation?> ResolveLocationAsync(string url, CancellationToken ct);
}
