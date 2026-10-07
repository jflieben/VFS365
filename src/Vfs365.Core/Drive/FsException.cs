namespace Vfs365.Core.Drive;

public enum FsError { NotFound, NameCollision, NotSameDevice, AccessDenied, DirectoryNotEmpty, ReadOnly }

/// <summary>A file system rule the engine enforces, for the front end to map to its own status codes.</summary>
public sealed class FsException(FsError error, string message) : Exception(message)
{
    public FsError Error { get; } = error;
}

/// <summary>Names that never reach SharePoint: application temp and lock files, Explorer's folder files.</summary>
public static class LocalOnlyNames
{
    public static bool IsLocalOnly(string name) =>
        name.StartsWith('~')
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
        || (name.Length == 8 && name.All(Uri.IsHexDigit)) // Excel's save temp file
        || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
        || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase);
}
