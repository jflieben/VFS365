namespace Vfs365.Core.Drive;

/// <summary>Invalid: the open handle's content is gone (deleted or replaced while it was open).</summary>
public enum FsError { NotFound, NameCollision, NotSameDevice, AccessDenied, DirectoryNotEmpty, ReadOnly, Invalid }

/// <summary>A file system rule the engine enforces, for the front end to map to its own status codes.</summary>
public sealed class FsException(FsError error, string message) : Exception(message)
{
    public FsError Error { get; } = error;
}

/// <summary>Names of application temp and lock files and Explorer's folder files.</summary>
public static class LocalOnlyNames
{
    /// <summary>Never reach SharePoint as new files: Office owner files, Word's temp files, Explorer's folder files.</summary>
    public static bool IsLocalOnly(string name) =>
        name.StartsWith("~$", StringComparison.Ordinal)
        || (name.StartsWith("~WR", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Names save patterns use for a moment: the local-only ones, a leading ~, .tmp, Excel's 8 hex digits. Real files have them too
    /// (20261024, ~Archive.docx), so a new file under such a name uploads when it is still there minutes after its close.
    /// </summary>
    public static bool IsTempLike(string name) =>
        IsLocalOnly(name)
        || name.StartsWith('~')
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || (name.Length == 8 && name.All(Uri.IsHexDigit));
}
