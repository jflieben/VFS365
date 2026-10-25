using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Vfs365.Core;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// What the VFS365 menu's verbs do, in the running agent: the Windows path Explorer gave becomes a volume path, the engine finds the
/// item, one Graph GET reads its links when the verb needs them, and the agent opens the URL, sets the clipboard or keeps a conflict
/// copy's version itself. Problems and results reach the user as notifications, in plain words.
/// </summary>
public sealed class VerbActions(DriveEngine engine, Func<string, string, CancellationToken, Task<ItemLinks?>> links, Func<string, string?> volumePath,
    Action<string> open, Action<string> copy, Action<string, string, bool> notify, Action<string> log)
{
    public async Task RunAsync(string verb, string path, CancellationToken ct)
    {
        var shown = Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path;
        try
        {
            if (volumePath(path) is not { } volume)
            {
                Problem("Verb.NotOnDrive", shown);
                return;
            }
            log($"Explorer menu: {verb} {volume}");
            if (await engine.GetEntryAsync(volume, ct) is not { } entry)
            {
                Problem("Verb.NotFound", shown);
                return;
            }
            switch (verb)
            {
                case "keep":
                    await KeepAsync(entry, ct);
                    return;
                case "compare":
                    await CompareAsync(entry, ct);
                    return;
            }
            if (await LinksOfAsync(entry, ct) is not { } found)
            {
                return;
            }
            switch (verb)
            {
                case "browser":
                    Open(found.WebUrl, entry);
                    break;
                case "share":
                    if (Open(found.WebUrl, entry))
                    {
                        notify(UiText.Get("Verb.Share.Title"), UiText.Get("Verb.ShareHint", entry.Name), false);
                    }
                    break;
                case "link":
                    if (Https(found.WebUrl) is { } url)
                    {
                        copy(url);
                        notify(UiText.Get("Verb.Copied.Title"), UiText.Get("Verb.Copied", entry.Name), false);
                    }
                    else
                    {
                        Problem("Verb.NotFound", entry.Name);
                    }
                    break;
                case "desktop":
                    if (entry.IsDirectory || !ExplorerVerbs.OfficeSchemes.ContainsKey(Path.GetExtension(entry.Name)))
                    {
                        Problem("Verb.NotOffice");
                    }
                    else if (Https(found.WebDavUrl) is { } file)
                    {
                        open(ItemUrls.Desktop(entry.Name, file)!);
                    }
                    else
                    {
                        Problem("Verb.NotFound", entry.Name);
                    }
                    break;
                case "versions":
                    if (entry.IsDirectory || found is not { SiteUrl: { } site, ListId: { } list, ListItemId: { } item } || Https(site) is null)
                    {
                        Problem("Verb.NoVersions", entry.Name);
                    }
                    else
                    {
                        open(ItemUrls.VersionHistory(site, list, item));
                    }
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"Explorer menu: {verb} {path} failed: {e.Message}");
            switch (e)
            {
                case RemoteException { Error: RemoteError.NotFound or RemoteError.Gone }:
                case FsException { Error: FsError.NotFound }:
                    Problem("Verb.NotFound", shown);
                    break;
                default:
                    Problem("Verb.Failed", shown, PlainCause.Text(PlainCause.Of(e.Message)));
                    break;
            }
        }
    }

    /// <summary>The copy's content becomes a new version of the original (<see cref="DriveEngine.KeepVersionAsync"/>).</summary>
    async Task KeepAsync(FsEntry copy, CancellationToken ct)
    {
        if (copy.IsDirectory || ConflictCopies.OriginalName(copy.Name) is not { } original)
        {
            Problem("Verb.NotCopy", copy.Name);
            return;
        }
        try
        {
            await engine.KeepVersionAsync(copy.Path, ct);
            notify(UiText.Get("Verb.Kept.Title"), UiText.Get("Verb.Kept", original), false);
        }
        catch (RemoteException e) when (e.Error == RemoteError.Conflict)
        {
            log($"Explorer menu: keep {copy.Path}: {e.Message}");
            Problem("Verb.ChangedAgain", original);
        }
    }

    /// <summary>
    /// Opens the original and the copy next to each other: Office files in their desktop app, others in the browser. Word's own
    /// comparison has no documented URL or command line, only its object model, so it isn't started.
    /// </summary>
    async Task CompareAsync(FsEntry copy, CancellationToken ct)
    {
        if (copy.IsDirectory || ConflictCopies.OriginalName(copy.Name) is not { } name)
        {
            Problem("Verb.NotCopy", copy.Name);
            return;
        }
        if (await engine.GetEntryAsync(copy.Path[..(copy.Path.LastIndexOf('\\') + 1)] + name, ct) is not { } original)
        {
            Problem("Verb.NotFound", name);
            return;
        }
        foreach (var entry in new[] { original, copy })
        {
            if (await LinksOfAsync(entry, ct) is not { } found)
            {
                return;
            }
            if (ExplorerVerbs.OfficeSchemes.ContainsKey(Path.GetExtension(entry.Name)) && Https(found.WebDavUrl) is { } file)
            {
                open(ItemUrls.Desktop(entry.Name, file)!);
            }
            else if (!Open(found.WebUrl, entry))
            {
                return;
            }
        }
    }

    /// <summary>The links of an entry (a library's top: its root folder), or null after telling the user why there are none.</summary>
    async Task<ItemLinks?> LinksOfAsync(FsEntry entry, CancellationToken ct)
    {
        var driveId = await engine.DriveIdOfAsync(entry, ct);
        if (driveId is null)
        {
            Problem("Verb.OwnFolder", entry.Name.Length > 0 ? entry.Name : entry.Path);
            return null;
        }
        var itemId = entry.ItemId ?? (entry.IsDirectory && entry.DrivePath.Length == 0 ? "root" : null);
        if (itemId is null)
        {
            Problem("Verb.NotUploaded", entry.Name);
            return null;
        }
        var found = await links(driveId, itemId, ct);
        if (found is null)
        {
            Problem("Verb.NotFound", entry.Name);
        }
        return found;
    }

    bool Open(string? url, FsEntry entry)
    {
        if (Https(url) is not { } safe)
        {
            Problem("Verb.NotFound", entry.Name);
            return false;
        }
        open(safe);
        return true;
    }

    /// <summary>Only https addresses from Graph are opened or copied.</summary>
    static string? Https(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;

    void Problem(string key, params object?[] args) => notify(UiText.Get("Verb.Menu"), UiText.Get(key, args), true);
}

/// <summary>
/// The running agent's side of Explorer's VFS365 menu: writes the verbs for the volume's current roots (the drive's letter, its UNC path
/// and the letters for folders, checked every 30 s, written when they change), answers the verb pipe and removes the verbs when
/// disposed (a clean stop).
/// </summary>
public sealed class ExplorerMenu : IDisposable
{
    readonly string exe, unc;
    readonly string? letter;
    readonly SessionDriveLetters? folders;
    readonly Action<string> log;
    readonly CancellationTokenSource stop = new();
    readonly Timer timer;
    readonly Task serving;
    readonly Lock gate = new();
    bool disposed;

    ExplorerMenu(string exe, string unc, string? letter, SessionDriveLetters? folders, VerbActions actions, Action<string> log)
    {
        (this.exe, this.unc, this.letter, this.folders, this.log) = (exe, unc, letter, folders, log);
        Update();
        timer = new Timer(_ => Update(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        serving = Task.Run(async () =>
        {
            try
            {
                await VerbPipe.ServeAsync(VerbPipe.CurrentName(), (verb, path) =>
                {
                    if (!ExplorerVerbs.IsVerb(verb) || path.Length is 0 or > 32767)
                    {
                        return "unknown request";
                    }
                    _ = Task.Run(() => actions.RunAsync(verb, path, stop.Token));
                    return null;
                }, log, stop.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log($"Explorer menu: the pipe stopped: {e.Message}"); // the drive goes on without the menu's requests
            }
        });
    }

    /// <summary>
    /// Starts the menu for a mounted drive; null (and said why) when vfs365-agent.exe isn't next to this program (a development build of
    /// vfs365.exe) or the registry refuses it.
    /// </summary>
    public static ExplorerMenu? Start(DriveEngine engine, DriveApi api, string unc, string? letter, SessionDriveLetters? folders,
        Action<string, string, bool> notify, Action<string> say, Action<string> log)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "vfs365-agent.exe");
        if (!File.Exists(exe))
        {
            say($"Explorer:   no VFS365 menu, {exe} is missing");
            return null;
        }
        var actions = new VerbActions(engine, api.GetLinksAsync,
            path => ExplorerVerbs.VolumePath(path, unc, letter, folders?.Folders() ?? new Dictionary<string, string>()),
            Shell, Clipboard.SetText, notify, log);
        try
        {
            return new ExplorerMenu(exe, unc, letter, folders, actions, log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            say($"Warning:    could not add the VFS365 menu in Explorer: {e.Message}");
            ExplorerVerbs.Remove(Registry.CurrentUser);
            return null;
        }
    }

    /// <summary>The roots the verbs apply to: "M:\", "\\VFS365\ann\", "Z:\".</summary>
    public static IReadOnlyList<string> Roots(string unc, string? letter, IEnumerable<string> folderLetters) =>
        [.. new[] { letter }.Concat(folderLetters).OfType<string>().Select(l => l.TrimEnd('\\') + @"\"), unc.TrimEnd('\\') + @"\"];

    void Update()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            try
            {
                if (ExplorerVerbs.Register(Registry.CurrentUser, exe, Roots(unc, letter, folders?.Folders().Keys ?? [])))
                {
                    log("Explorer menu: written for the drive's current letters");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                log($"Explorer menu: could not write it: {e.Message}");
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        timer.Dispose();
        stop.Cancel();
        try
        {
            ExplorerVerbs.Remove(Registry.CurrentUser);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            log($"Explorer menu: could not remove it: {e.Message}");
        }
        serving.Wait(TimeSpan.FromSeconds(2)); // never faults; stop stays undisposed for verbs still running
    }

    /// <summary>Opens an https or Office URL with the user's default handler.</summary>
    static void Shell(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}

/// <summary>Text to the clipboard from a short STA thread with a message-only window (the clipboard needs an owner window).</summary>
static class Clipboard
{
    public static void SetText(string text)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Set(text);
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(5)) || failure is not null)
        {
            throw failure ?? new IOException("the clipboard didn't answer");
        }
    }

    static void Set(string text)
    {
        var window = CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, new IntPtr(-3), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // HWND_MESSAGE
        try
        {
            // Another program may hold the clipboard for a moment
            for (var attempt = 0; !OpenClipboard(window); attempt++)
            {
                if (attempt == 10)
                {
                    throw new IOException("the clipboard is in use by another program");
                }
                Thread.Sleep(50);
            }
            try
            {
                EmptyClipboard();
                var bytes = (text.Length + 1) * 2;
                var memory = GlobalAlloc(0x2, (nuint)bytes); // GMEM_MOVEABLE
                if (memory == IntPtr.Zero)
                {
                    throw new OutOfMemoryException();
                }
                var target = GlobalLock(memory);
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
                GlobalUnlock(memory);
                if (SetClipboardData(13, memory) == IntPtr.Zero) // CF_UNICODETEXT; the system owns the memory once it is set
                {
                    GlobalFree(memory);
                    throw new IOException("the clipboard refused the link");
                }
            }
            finally
            {
                CloseClipboard();
            }
        }
        finally
        {
            if (window != IntPtr.Zero)
            {
                DestroyWindow(window);
            }
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll")]
    static extern IntPtr GlobalFree(IntPtr memory);
}
