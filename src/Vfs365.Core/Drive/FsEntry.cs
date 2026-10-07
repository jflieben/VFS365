namespace Vfs365.Core.Drive;

/// <summary>A file or folder as the front end sees it. Paths are '\'-separated from the volume root.</summary>
public sealed class FsEntry
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset Modified { get; init; }
    public bool ReadOnly { get; init; }

    /// <summary>Listed but not to be opened (database file policy Block).</summary>
    public bool Blocked { get; init; }

    /// <summary>Null for virtual folders and files that exist only locally so far.</summary>
    public string? ItemId { get; init; }

    /// <summary>Changes when the content changes (cTag, else eTag).</summary>
    public string? ContentTag { get; init; }

    /// <summary>Local file holding this file's current content while it is being changed.</summary>
    public string? StagingPath { get; init; }

    /// <summary>Graph drive of a drive item; null for virtual folders.</summary>
    public string? DriveId { get; internal init; }

    /// <summary>Path inside the drive, '/'-separated, "" for its root.</summary>
    public string DrivePath { get; internal init; } = "";

    internal VirtualFolder? Folder { get; init; }
    internal DriveMount? Drive { get; init; }
}
