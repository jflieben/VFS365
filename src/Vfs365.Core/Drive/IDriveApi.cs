namespace Vfs365.Core.Drive;

/// <summary>An item. ParentId is set where Graph returned it (single items and changes, not listings, where the folder is known).</summary>
public sealed record DriveItemInfo(string Id, string Name, bool IsFolder, long Size, DateTimeOffset Created, DateTimeOffset Modified, string? ETag, string? CTag,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ParentId = null);

/// <summary>
/// A download serves other content than the listing said: another length (the file changed in Microsoft 365 since it was listed, or
/// SharePoint serves an Office file with properties written into it), or another version (<see cref="OtherVersion"/>).
/// <see cref="Current"/> is the item as served.
/// </summary>
public sealed class ContentChangedException(DriveItemInfo current, long listed, bool otherVersion = false)
    : IOException(otherVersion
        ? "changed in Microsoft 365 since it was listed (another version is served)"
        : $"changed in Microsoft 365 since it was listed ({listed} bytes listed, {current.Size} served)")
{
    public DriveItemInfo Current { get; } = current;
    public long Listed { get; } = listed;
    public bool OtherVersion { get; } = otherVersion;
}

/// <summary>Compares the content tag a listing gave (cTag, else eTag) with the item as it is now.</summary>
public static class ContentTags
{
    /// <summary>
    /// True when the item now holds another version than <paramref name="expected"/> names. Graph tags are "{id},version" (eTag) and
    /// "c:{id},version" (cTag): a tag of the same kind and item with another version differs. Tags in another form never count as
    /// different, so an unknown format can't fail every download.
    /// </summary>
    public static bool Differs(string? expected, DriveItemInfo current) =>
        expected is not null && expected != current.CTag && expected != current.ETag && (SameStem(expected, current.CTag) || SameStem(expected, current.ETag));

    static bool SameStem(string expected, string? current) =>
        current is not null && expected.LastIndexOf(',') is var split and > 0 && current.LastIndexOf(',') == split
        && expected.AsSpan(0, split).SequenceEqual(current.AsSpan(0, split));
}

/// <summary>One change from a drive's delta feed. Item is null for deletions (and for items that are neither file nor folder).</summary>
public sealed record DriveChange(string Id, string? ParentId, DriveItemInfo? Item, bool Deleted);

/// <summary>A page of changes: follow NextLink, or keep DeltaLink for the next round.</summary>
public sealed record DeltaPage(IReadOnlyList<DriveChange> Changes, string? NextLink, string? DeltaLink);

/// <summary>Upload into an existing item (keeps its ID, versions and sharing) or as a new file that must not exist yet.</summary>
public sealed record UploadTarget(string? ItemId, string? IfMatch, string? ParentPath, string? Name)
{
    /// <summary>The cTag of the version the upload replaces: before the last fragment of a session, an item with other content fails it.</summary>
    public string? ContentTag { get; init; }

    public static UploadTarget Existing(DriveItemInfo item) => new(item.Id, item.ETag, null, null) { ContentTag = item.CTag };

    public static UploadTarget New(string parentPath, string name) => new(null, null, parentPath, name);
}

/// <summary>A resumable upload. Fragments go in order; each is a multiple of 320 KiB except the last.</summary>
public sealed record UploadSession(Uri UploadUrl)
{
    public const int FragmentSize = 16 * 320 * 1024; // 5 MiB

    /// <summary>Fragments are multiples of this, except the last.</summary>
    public const int FragmentUnit = 320 * 1024;

    /// <summary>Smaller files go up in one request.</summary>
    public const long SimpleUploadLimit = 4 * 1024 * 1024;

    /// <summary>Where the session uploads to, for the check before its last fragment.</summary>
    public string? DriveId { get; init; }
    public UploadTarget? Target { get; init; }
}

/// <summary>Drive calls the engine needs. Paths are relative to the drive root, '/'-separated, "" for the root. Implemented in Vfs365.Graph.</summary>
public interface IDriveApi
{
    /// <summary>A folder's children, one page at a time as they arrive.</summary>
    IAsyncEnumerable<IReadOnlyList<DriveItemInfo>> ListChildrenAsync(string driveId, string folderPath, CancellationToken ct);

    /// <summary>A delta link for "from now on", without listing what exists.</summary>
    Task<string> GetLatestDeltaLinkAsync(string driveId, CancellationToken ct);

    /// <summary>Changes after a delta (or next) link. Throws RemoteException(Gone) when the link expired.</summary>
    Task<DeltaPage> GetDeltaAsync(string link, CancellationToken ct);

    /// <summary>Null when nothing exists at the path. "" returns the root folder.</summary>
    Task<DriveItemInfo?> GetItemAsync(string driveId, string path, CancellationToken ct);

    /// <summary>The item as it is now, wherever it is; null when it was deleted.</summary>
    Task<DriveItemInfo?> GetItemByIdAsync(string driveId, string itemId, CancellationToken ct);

    /// <summary>
    /// Content from <paramref name="offset"/> to the end, as it arrives, of the version <paramref name="contentTag"/> names (null: any).
    /// Throws <see cref="ContentChangedException"/> when the item now holds another version (<see cref="ContentTags.Differs"/>) or the
    /// content served isn't <paramref name="length"/> bytes long.
    /// </summary>
    Task<Stream> OpenReadAsync(string driveId, string itemId, string? contentTag, long offset, long length, CancellationToken ct);

    /// <summary>Whole-file upload: one request up to <see cref="UploadSession.SimpleUploadLimit"/>, else a session.</summary>
    Task<DriveItemInfo> UploadAsync(string driveId, UploadTarget target, string sourceFile, CancellationToken ct);

    Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct);

    /// <summary>
    /// Uploads bytes [offset, offset + length) of the file; returns the item after the last fragment. Before the last one an item
    /// whose content changed since the session began fails it (Conflict; NotFound when deleted).
    /// </summary>
    Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct);

    Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct);

    Task<DriveItemInfo> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct);

    /// <summary>Rename and/or move within the drive.</summary>
    Task<DriveItemInfo> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct);

    /// <summary>To the recycle bin.</summary>
    Task DeleteAsync(string driveId, string itemId, CancellationToken ct);
}
