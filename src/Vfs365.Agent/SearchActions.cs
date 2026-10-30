using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Searching with Microsoft 365 instead of reading every folder of the drive (decision 48): the scope of a folder on the drive (one or
/// two Graph GETs for its links), the SharePoint search page for it (<see cref="SearchUrls.For"/>), asking for the words (the verb, the
/// tray) and opening the page. The words are never logged: logs go into support bundles.
/// </summary>
public sealed class SearchActions(DriveEngine engine, Func<string, string, CancellationToken, Task<ItemLinks?>> links, Func<string, string?> volumePath,
    MyDrive? oneDrive, string tenantRoot, Func<string, CancellationToken, Task<string?>> ask, Action<string> open, Action<string, string, bool> notify, Action<string> log)
{
    /// <summary>The verb on a folder: asks for words and opens the folder's search.</summary>
    public async Task AskAndSearchAsync(string windowsPath, CancellationToken ct)
    {
        var shown = Path.GetFileName(windowsPath.TrimEnd('\\')) is { Length: > 0 } name ? name : windowsPath;
        try
        {
            if (volumePath(windowsPath) is not { } volume || await FolderAsync(volume, ct) is not { } folder)
            {
                Problem("Verb.NotOnDrive", shown);
                return;
            }
            var words = SearchTerms.Clean(await ask(UiText.Get("Search.Prompt", folder.Name.Length > 0 ? folder.Name : shown), ct));
            if (words.Length > 0)
            {
                await OpenAsync(folder, words, "the menu", ct);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(shown, e);
        }
    }

    /// <summary>The tray: asks for words and searches all of Microsoft 365.</summary>
    public async Task AskAndSearchEverythingAsync(CancellationToken ct)
    {
        try
        {
            var words = SearchTerms.Clean(await ask(UiText.Get("Search.Prompt", UiText.Get("Search.Everything")), ct));
            if (words.Length > 0)
            {
                Open(new SearchScope(SearchArea.Everything, tenantRoot), words, "the tray", @"\");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(UiText.Get("Search.Everything"), e);
        }
    }

    /// <summary>
    /// Explorer searched <paramref name="volume"/> for <paramref name="terms"/> (<see cref="ExplorerSearchWatcher"/>): opens that search in
    /// Microsoft 365, through <paramref name="throughExplorer"/> when it can (true: opened). Without words left (only filters Microsoft 365
    /// doesn't know) it asks for them. The words searched, or null when nothing opened.
    /// </summary>
    public async Task<string?> HandOffAsync(string volume, string terms, Func<string, bool>? throughExplorer, CancellationToken ct)
    {
        try
        {
            if (await FolderAsync(volume, ct) is not { } folder)
            {
                return null;
            }
            var words = SearchTerms.Clean(terms);
            if (words.Length == 0)
            {
                words = SearchTerms.Clean(await ask(UiText.Get("Search.Prompt", folder.Name.Length > 0 ? folder.Name : volume), ct));
            }
            return words.Length > 0 && await OpenAsync(folder, words, "Explorer", ct, throughExplorer) ? words : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed(volume, e);
            return null;
        }
    }

    /// <summary>The folder at a volume path; a file's folder.</summary>
    async Task<FsEntry?> FolderAsync(string volume, CancellationToken ct)
    {
        var entry = await engine.GetEntryAsync(volume, ct);
        if (entry is { IsDirectory: false })
        {
            var parent = volume.LastIndexOf('\\') is > 0 and var at ? volume[..at] : @"\";
            entry = await engine.GetEntryAsync(parent, ct);
        }
        return entry is { IsDirectory: true } ? entry : null;
    }

    async Task<bool> OpenAsync(FsEntry folder, string words, string from, CancellationToken ct, Func<string, bool>? throughExplorer = null) =>
        Open(await ScopeAsync(folder, engine, links, oneDrive?.DriveId ?? "", tenantRoot, ct), words, from, folder.Path, throughExplorer);

    bool Open(SearchScope scope, string words, string from, string where, Func<string, bool>? throughExplorer = null)
    {
        if (SearchUrls.For(scope, words) is not { } url)
        {
            log($"Search: no Microsoft 365 search page for {where} ({scope.Area})");
            Problem("Search.NoPage", where);
            return false;
        }
        var viaExplorer = throughExplorer?.Invoke(url) == true;
        if (!viaExplorer)
        {
            open(url);
        }
        log($"Search: {from} searched {where} ({scope.Area}, {words.Length} characters), opened in Microsoft 365{(viaExplorer ? " through Explorer" : "")}");
        return true;
    }

    /// <summary>
    /// The scope of a folder: inside a drive the folder or the library's top (its links say where); a site's folder (its children are
    /// libraries) its site; the root, Sites and Shared with me everything.
    /// </summary>
    public static async Task<SearchScope> ScopeAsync(FsEntry folder, DriveEngine engine, Func<string, string, CancellationToken, Task<ItemLinks?>> links,
        string oneDriveId, string tenantRoot, CancellationToken ct)
    {
        if (await engine.DriveIdOfAsync(folder, ct) is { } driveId)
        {
            var top = folder.DrivePath.Length == 0;
            var found = await links(driveId, folder.ItemId ?? "root", ct);
            var library = top ? found : await links(driveId, "root", ct);
            var area = top ? driveId == oneDriveId ? SearchArea.OneDrive : SearchArea.Library : SearchArea.Folder;
            return new SearchScope(area, tenantRoot, found?.SiteUrl, found?.WebUrl, library?.WebUrl);
        }
        if (folder.Path.Equals(@"\" + DriveNamespace.SharedFolderName, StringComparison.OrdinalIgnoreCase))
        {
            return new SearchScope(SearchArea.Everything, tenantRoot);
        }
        string? site = null;
        foreach (var child in await engine.ListAsync(folder, ct))
        {
            if (!child.IsDirectory || await engine.DriveIdOfAsync(child, ct) is not { } childDrive)
            {
                return new SearchScope(SearchArea.Everything, tenantRoot);
            }
            site ??= (await links(childDrive, "root", ct))?.SiteUrl;
        }
        return site is null ? new SearchScope(SearchArea.Everything, tenantRoot) : new SearchScope(SearchArea.Site, tenantRoot, site);
    }

    void Failed(string shown, Exception e)
    {
        log($"Search: {shown} failed: {e.Message}");
        Problem("Verb.Failed", shown, PlainCause.Text(PlainCause.Of(e.Message)));
    }

    void Problem(string key, params object?[] args) => notify(UiText.Get("Search.Title"), UiText.Get(key, args), true);
}
