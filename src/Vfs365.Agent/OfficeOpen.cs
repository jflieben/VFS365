using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>How an Office open ends: by URL, the copy on the drive (Office's own Open verb, or Windows' Open with), or not at all.</summary>
public enum OfficeRoute { DesktopApp, Browser, Local, OpenWith, Cancelled }

public sealed record OfficeOutcome(OfficeRoute Route, string Reason, string? Url = null);

/// <summary>The answer to "no app for this type": open in the browser, choose an app on this PC, or nothing.</summary>
public enum MissingChoice { Browser, ThisPc, Cancel }

/// <summary>
/// What the Office verb (<see cref="OfficeVerbs"/>) does in the running agent (decision 50): the file opens by URL, in its desktop app
/// or the browser, only when Microsoft 365 has exactly what the drive shows: uploaded, not empty, nothing staged here, and its links
/// within <see cref="Budget"/>. Anything else, and any failure, opens the copy on the drive with Office's own Open verb (Windows'
/// Open with for a type without an app), never nothing.
/// </summary>
public sealed class OfficeOpen(DriveEngine engine, Func<string, string, CancellationToken, Task<ItemLinks?>> links, Func<string, string?> volumePath,
    OfficeSettings settings, Func<string, OfficeAssociation> associate, Action<string> openUrl, Action<string, bool> openLocal,
    Func<string, string, CancellationToken, Task<MissingChoice>> ask, Action<string> log)
{
    /// <summary>The pipe request; not one of the VFS365 submenu's verbs.</summary>
    public const string Verb = "open";

    /// <summary>The client waits this long to connect and as long again for the answer, then opens the file itself.</summary>
    public static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(1);

    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(4);

    /// <summary>How long the lookup and the links call may take together; slower (offline, signed out, throttled) opens the copy here.</summary>
    public TimeSpan Budget { get; init; } = DefaultBudget;

    /// <summary>A cancelled question also cancels the opens of that type that waited for it (several files selected), for this long.</summary>
    public TimeSpan CancelHolds { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Answers to the question for a type without an app, per type for this run; one question at a time.</summary>
    readonly ConcurrentDictionary<string, (MissingChoice Choice, DateTimeOffset At)> answers = new(StringComparer.OrdinalIgnoreCase);
    readonly SemaphoreSlim asking = new(1, 1);

    public async Task<OfficeOutcome> RunAsync(string path, CancellationToken ct)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        OfficeOutcome outcome;
        try
        {
            outcome = await DecideAsync(path, extension, ct);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            outcome = new(OfficeRoute.Local, $"failed: {PlainCause.Of(e.Message)}");
        }
        var shown = settings.LogPaths ? volumePath(path) ?? path : $"a {extension} file";
        log($"Office open: {shown}: {Describe(outcome)}");
        try
        {
            switch (outcome.Route)
            {
                case OfficeRoute.DesktopApp or OfficeRoute.Browser:
                    try
                    {
                        openUrl(outcome.Url!);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // The message names the address, so the file too
                        log($"Office open: {shown}: the address didn't open ({(settings.LogPaths ? e.Message : $"0x{e.HResult:X8}")}); the copy on the drive opens");
                        openLocal(path, associate(extension).Handler == OfficeHandler.Missing);
                    }
                    break;
                case OfficeRoute.Local or OfficeRoute.OpenWith:
                    openLocal(path, outcome.Route == OfficeRoute.OpenWith);
                    break;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log($"Office open: {shown}: Windows didn't open it: {e.Message}");
        }
        return outcome;
    }

    async Task<OfficeOutcome> DecideAsync(string path, string extension, CancellationToken ct)
    {
        if (settings.Mode == OfficeFilesMode.Local)
        {
            return new(OfficeRoute.Local, "policy OfficeFiles is Local");
        }
        if (!settings.Types.Contains(extension, StringComparer.OrdinalIgnoreCase) || OfficeTypes.AppOf(extension) is not { } app)
        {
            return new(OfficeRoute.Local, "not a type in OfficeFileTypes");
        }
        var handler = associate(extension).Handler;
        if (handler == OfficeHandler.Other)
        {
            return new(OfficeRoute.Local, "another app opens this type");
        }
        var missing = handler == OfficeHandler.Missing;
        OfficeOutcome Here(string reason) => new(missing ? OfficeRoute.OpenWith : OfficeRoute.Local, reason);
        if (missing && settings.Missing == OfficeMissingMode.Local)
        {
            return Here("no app for this type, policy OfficeMissing is Local");
        }
        if (volumePath(path) is not { } volume)
        {
            return Here("not on the drive");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        FsEntry? entry;
        ItemLinks? found;
        try
        {
            entry = await engine.GetEntryAsync(volume, budget.Token).WaitAsync(budget.Token);
            if (entry is null || entry.IsDirectory)
            {
                return Here("not found");
            }
            if (entry.ItemId is not { } itemId)
            {
                return Here("not uploaded yet");
            }
            if (entry.Size == 0)
            {
                return Here("empty, so Office makes it a new document"); // Explorer's New menu
            }
            if (engine.IsStaged(entry))
            {
                return Here("changed or being written on this PC");
            }
            if (await engine.DriveIdOfAsync(entry, budget.Token).WaitAsync(budget.Token) is not { } driveId)
            {
                return Here("not in a library");
            }
            found = await links(driveId, itemId, budget.Token).WaitAsync(budget.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException && !ct.IsCancellationRequested)
        {
            return Here($"Microsoft 365 didn't answer within {Budget.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} s");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
        {
            return Here($"Microsoft 365 didn't give its address: {PlainCause.Of(e.Message)}");
        }
        if (found is null)
        {
            return Here("not found in Microsoft 365");
        }

        if (missing)
        {
            if (settings.Missing == OfficeMissingMode.Ask)
            {
                MissingChoice choice;
                await asking.WaitAsync(ct);
                try
                {
                    if (!answers.TryGetValue(extension, out var known) || (known.Choice == MissingChoice.Cancel && DateTimeOffset.UtcNow - known.At >= CancelHolds))
                    {
                        known = (await ask(app.Name, entry.Name, ct), DateTimeOffset.UtcNow);
                        answers[extension] = known;
                    }
                    choice = known.Choice;
                }
                finally
                {
                    asking.Release();
                }
                switch (choice)
                {
                    case MissingChoice.Cancel:
                        return new(OfficeRoute.Cancelled, "the question was cancelled");
                    case MissingChoice.ThisPc:
                        return Here("no app for this type, an app on this PC was chosen");
                }
            }
            return Https(found.WebUrl) is { } web ? new(OfficeRoute.Browser, $"no {app.Name} on this PC", web) : Here("no web address");
        }
        if (settings.Mode == OfficeFilesMode.Browser)
        {
            return Https(found.WebUrl) is { } web ? new(OfficeRoute.Browser, "policy OfficeFiles is Browser", web) : Here("no web address");
        }
        if (Https(found.WebDavUrl) is not { } file)
        {
            return Here("no file address");
        }
        return OfficeTypes.TooLong(extension, file)
            ? Here("its address is too long for Office")
            : new(OfficeRoute.DesktopApp, "uploaded and unchanged here", OfficeTypes.DesktopUrl(extension, file));
    }

    static string Describe(OfficeOutcome outcome) => outcome.Route switch
    {
        OfficeRoute.DesktopApp => $"in the desktop app from Microsoft 365 ({outcome.Reason})",
        OfficeRoute.Browser => $"in the browser ({outcome.Reason})",
        OfficeRoute.Local => $"the copy on the drive ({outcome.Reason})",
        OfficeRoute.OpenWith => $"Windows asks which app ({outcome.Reason})",
        _ => $"nothing ({outcome.Reason})",
    };

    /// <summary>Only https addresses from Graph are opened.</summary>
    static string? Https(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? url : null;

    /// <summary>
    /// "vfs365-agent.exe verb open &lt;path&gt;", run by Explorer: hands the open to the agent, and when no agent takes it in time (not
    /// running, busy, refused) opens the copy on the drive itself, with Office's own verb or, for a type without an app, Windows' Open
    /// with. Never a message of its own: Windows reports what it can't open.
    /// </summary>
    internal static int RunClient(string path, Func<string, bool> send, Func<string, OfficeAssociation> associate, Action<string, bool> openLocal)
    {
        if (send(path))
        {
            return 0;
        }
        try
        {
            openLocal(path, associate(Path.GetExtension(path)).Handler == OfficeHandler.Missing);
            return 0;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return 1;
        }
    }
}

/// <summary>Opens a file on the PC through Windows' shell, on a short STA thread (ShellExecuteEx may use COM).</summary>
public static class LocalOpen
{
    /// <summary>
    /// <paramref name="chooseApp"/> false: the verb "open", the app's own (never VFS365.Open, so it doesn't come back); true: Windows'
    /// Open with. Completes once Windows started it (SEE_MASK_NOASYNC: DDE included), faults when Windows couldn't.
    /// </summary>
    public static Task OpenAsync(string path, bool chooseApp)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                AllowSetForegroundWindow(-1); // ASFW_ANY: the app may come to the front; does nothing without the right
                var info = new ShellExecuteInfo
                {
                    Size = Marshal.SizeOf<ShellExecuteInfo>(),
                    Mask = 0x100, // SEE_MASK_NOASYNC
                    Verb = chooseApp ? "openas" : "open",
                    File = path,
                    Show = 1, // SW_SHOWNORMAL
                };
                if (!ShellExecuteExW(ref info))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                done.TrySetResult();
            }
            catch (Exception e)
            {
                done.TrySetException(e);
            }
        })
        { IsBackground = true, Name = "VFS365 open" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellExecuteInfo
    {
        public int Size;
        public uint Mask;
        public IntPtr Window;
        public string? Verb;
        public string File;
        public string? Parameters;
        public string? Directory;
        public int Show;
        public IntPtr Instance;
        public IntPtr IdList;
        public string? Class;
        public IntPtr ClassKey;
        public uint HotKey;
        public IntPtr IconOrMonitor;
        public IntPtr Process;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);
}
