namespace Vfs365.Core.Drive;

/// <summary>
/// What the drive does with multi-user database files. Their byte-range locks don't leave the PC, so two people opening
/// the same database "exclusively" on two PCs each write their own copy and one loses.
/// </summary>
public enum DatabaseFilePolicy
{
    /// <summary>Like any other file.</summary>
    Allow,

    /// <summary>Open for reading only; saving, creating and their lock files are refused.</summary>
    ReadOnly,

    /// <summary>Listed, but opening is refused.</summary>
    Block,
}

public static class DatabaseFiles
{
    /// <summary>Access (and its lock files), QuickBooks company and network files, SQLite.</summary>
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".accdb", ".accde", ".accdr", ".mdb", ".mde", ".laccdb", ".ldb",
        ".qbw", ".qbb", ".nd", ".tlg",
        ".sqlite", ".sqlite3", ".db3",
    };

    public static bool IsDatabase(string name) => Extensions.Contains(Path.GetExtension(name));
}
