namespace Vfs365.Core.Drive;

public sealed record DriveItemInfo(string Id, string Name, bool IsFolder, long Size, DateTimeOffset Created, DateTimeOffset Modified, string? ETag, string? CTag);

/// <summary>One change from a drive's delta feed. Item is null for deletions (and for items that are neither file nor folder).</summary>
public sealed record DriveChange(string Id, string? ParentId, DriveItemInfo? Item, bool Deleted);

/// <summary>A page of changes: follow NextLink, or keep DeltaLink for the next round.</summary>
public sealed record DeltaPage(IReadOnlyList<DriveChange> Changes, string? NextLink, string? DeltaLink);

/// <summary>Upload into an existing item (keeps its ID, versions and sharing) or as a new file that must not exist yet.</summary>
public sealed record UploadTarget(string? ItemId, string? IfMatch, string? ParentPath, string? Name)
{
    public static UploadTarget Existing(DriveItemInfo item) => new(item.Id, item.ETag, null, null);

    public static UploadTarget New(string parentPath, string name) => new(null, null, parentPath, name);
}

/// <summary>A resumable upload. Fragments go in order; each is a multiple of 320 KiB except the last.</summary>
public sealed record UploadSession(Uri UploadUrl)
{
    public const int FragmentSize = 16 * 320 * 1024; // 5 MiB

    /// <summary>Smaller files go up in one request.</summary>
    public const long SimpleUploadLimit = 4 * 1024 * 1024;
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

    /// <summary>Content from <paramref name="offset"/> to the end, as it arrives.</summary>
    Task<Stream> OpenReadAsync(string driveId, string itemId, long offset, CancellationToken ct);

    /// <summary>Whole-file upload: one request up to <see cref="UploadSession.SimpleUploadLimit"/>, else a session.</summary>
    Task<DriveItemInfo> UploadAsync(string driveId, UploadTarget target, string sourceFile, CancellationToken ct);

    Task<UploadSession> CreateUploadSessionAsync(string driveId, UploadTarget target, CancellationToken ct);

    /// <summary>Uploads bytes [offset, offset + length) of the file; returns the item after the last fragment.</summary>
    Task<DriveItemInfo?> UploadFragmentAsync(UploadSession session, string sourceFile, long offset, long length, long totalSize, CancellationToken ct);

    Task CancelUploadSessionAsync(UploadSession session, CancellationToken ct);

    Task<DriveItemInfo> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct);

    /// <summary>Rename and/or move within the drive.</summary>
    Task<DriveItemInfo> MoveAsync(string driveId, string itemId, string newParentId, string newName, CancellationToken ct);

    /// <summary>To the recycle bin.</summary>
    Task DeleteAsync(string driveId, string itemId, CancellationToken ct);
}
