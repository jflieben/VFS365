namespace Vfs365.Core.Drive;

public sealed partial class DriveEngine
{
    /// <summary>
    /// Raised when a folder listing starts (Explorer, an app, a search crawl); loads ahead, refreshes and change feeds don't raise it.
    /// Raised on file system threads: handlers only note it and return, and never throw. The Explorer search watcher wakes on it.
    /// </summary>
    public event Action? FolderListed;
}
