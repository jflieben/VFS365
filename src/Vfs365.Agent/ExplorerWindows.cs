using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Vfs365.Agent;

/// <summary>
/// Explorer's open windows and tabs through the Shell Windows object (COM, served by explorer.exe in this session): where each one is,
/// and navigating one elsewhere. IWebBrowser2 is late bound (IDispatch by name), so no vtable or VARIANT layout differs between x64 and
/// ARM64; the parsing name comes from the window's shell view. Callers catch COMException (Explorer restarting) and others.
/// Every wrapper (RCW) a call returns is released as soon as it is read, in a finally: <see cref="Release"/> drops one reference, which
/// balances the one each marshalling into managed code added; <see cref="ReleaseAll"/> ends a wrapper this code owns.
/// </summary>
internal static partial class ExplorerWindows
{
    static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
    const uint DesktopAbsoluteParsing = 0x80028000; // SIGDN_DESKTOPABSOLUTEPARSING
    const int RpcCallCanceled = unchecked((int)0x80010002); // RPC_E_CALL_CANCELED

    /// <summary>
    /// A window or tab. Browser: its IWebBrowser2 (for <see cref="Navigate"/>). Identity: its COM identity in this process, which tells
    /// tabs of one window apart. ParsingName: the shown folder's parsing name (a search: see <see cref="ExplorerSearch.FromView"/>).
    /// </summary>
    public sealed record Window(object Browser, long Identity, IntPtr Hwnd, uint ProcessId, uint SessionId, string ClassName, string? LocationUrl,
        string? LocationName, string? ParsingName);

    /// <summary>What a tab fixes when it opens: where it lives. Identity: see <see cref="Window"/>.</summary>
    public sealed record TabInfo(long Identity, IntPtr Hwnd, uint ProcessId, uint SessionId, string ClassName);

    /// <summary>What a tab shows now: its LocationURL and LocationName, and the shown folder's parsing name.</summary>
    public sealed record View(string? LocationUrl, string? LocationName, string? ParsingName);

    public static object Open() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!) ?? throw new COMException("no Shell Windows object");

    /// <summary>File Explorer's window classes; other windows in Shell Windows (Internet Explorer) show no folder.</summary>
    public static bool IsFileExplorer(string className) => className is "CabinetWClass" or "ExploreWClass";

    /// <summary>
    /// The windows and tabs, at most <paramref name="max"/>, as <see cref="Describe"/>d windows whose wrappers the caller keeps; entries
    /// that fail on their own are left out (problem says why).
    /// </summary>
    public static List<Window> List(object shellWindows, int max, Action<string>? problem = null)
    {
        var list = new List<Window>();
        foreach (var browser in Items(shellWindows, max, problem))
        {
            try
            {
                list.Add(Describe(browser));
            }
            catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                Release(browser);
                problem?.Invoke($"window: {e.Message}");
            }
        }
        return list;
    }

    /// <summary>
    /// The windows and tabs, at most <paramref name="max"/>, as their IWebBrowser2 wrappers. The caller owns each one and releases it
    /// (<see cref="Release"/>) unless it keeps it; entries that fail on their own are left out (problem says why).
    /// </summary>
    public static List<object> Items(object shellWindows, int max, Action<string>? problem = null)
    {
        dynamic windows = shellWindows;
        var count = Math.Min((int)windows.Count, max);
        var list = new List<object>(count);
        for (var i = 0; i < count; i++)
        {
            try
            {
                object? browser = windows.Item(i);
                if (browser is not null)
                {
                    list.Add(browser);
                }
            }
            catch (Exception e) when (e is COMException or InvalidCastException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
            {
                problem?.Invoke($"window {i}: {e.Message}");
            }
        }
        return list;
    }

    static Window Describe(object browser)
    {
        var info = Identify(browser);
        var view = ReadView(browser);
        return new Window(browser, info.Identity, info.Hwnd, info.ProcessId, info.SessionId, info.ClassName, view.LocationUrl, view.LocationName, view.ParsingName);
    }

    /// <summary>The COM identity of a window or tab in this process (the same for every wrapper of the object).</summary>
    public static long IdentityOf(object browser)
    {
        var unknown = Marshal.GetIUnknownForObject(browser);
        Marshal.Release(unknown);
        return unknown.ToInt64();
    }

    /// <summary>Where a window or tab lives: window handle, process, session, window class (one call into Explorer).</summary>
    public static TabInfo Identify(object browser)
    {
        dynamic window = browser;
        var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        _ = ProcessIdToSessionId(pid, out var session);
        var classBuffer = new char[64];
        var className = new string(classBuffer, 0, Math.Max(0, GetClassNameW(hwnd, classBuffer, classBuffer.Length)));
        return new TabInfo(IdentityOf(browser), hwnd, pid, session, className);
    }

    /// <summary>What a window or tab shows now.</summary>
    public static View ReadView(object browser)
    {
        dynamic window = browser;
        string? url = null, name = null;
        try
        {
            url = window.LocationURL;
            name = window.LocationName;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException && !Cancelled(e))
        {
        }
        return new View(url, name, ParsingName(browser));
    }

    /// <summary>A call the watchdog cancelled because Explorer didn't answer; callers let it pass instead of calling on.</summary>
    public static bool Cancelled(Exception e) => e is COMException { HResult: RpcCallCanceled };

    /// <summary>
    /// Drops one reference of a wrapper a call returned (it ends the wrapper when that was the last one). Never throws; null and
    /// objects that aren't COM wrappers are left alone.
    /// </summary>
    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            try
            {
                Marshal.ReleaseComObject(com);
            }
            catch (Exception e) when (e is ArgumentException or InvalidComObjectException)
            {
            }
        }
    }

    /// <summary>Ends a wrapper this code owns whatever its reference count; uses of it afterwards fail with InvalidComObjectException.</summary>
    public static void ReleaseAll(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            try
            {
                Marshal.FinalReleaseComObject(com);
            }
            catch (Exception e) when (e is ArgumentException or InvalidComObjectException)
            {
            }
        }
    }

    /// <summary>The shown folder's parsing name: window, top-level browser, active shell view, its folder, the folder's ID list.</summary>
    public static string? ParsingName(object browser)
    {
        var pidl = IntPtr.Zero;
        object? found = null, view = null, folder = null;
        try
        {
            var service = TopLevelBrowser;
            var shellBrowserId = typeof(IShellBrowser).GUID;
            if (browser is not IServiceProvider provider || provider.QueryService(ref service, ref shellBrowserId, out found) != 0
                || found is not IShellBrowser shellBrowser)
            {
                return null;
            }
            shellBrowser.QueryActiveShellView(out view);
            if (view is not IFolderView folderView)
            {
                return null;
            }
            var persistId = typeof(IPersistFolder2).GUID;
            folderView.GetFolder(ref persistId, out folder);
            if (folder is not IPersistFolder2 persist)
            {
                return null;
            }
            persist.GetCurFolder(out pidl);
            if (pidl == IntPtr.Zero || SHGetNameFromIDList(pidl, DesktopAbsoluteParsing, out var text) != 0)
            {
                return null;
            }
            try
            {
                return Marshal.PtrToStringUni(text);
            }
            finally
            {
                Marshal.FreeCoTaskMem(text);
            }
        }
        catch (Exception e) when (e is COMException or InvalidCastException && !Cancelled(e))
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
            Release(folder);
            Release(view);
            Release(found);
        }
    }

    /// <summary>Shows <paramref name="location"/> (a path, as Explorer named it) in the window or tab.</summary>
    public static void Navigate(object browser, string location) => ((dynamic)browser).Navigate2(location);

    public static void GoBack(object browser) => ((dynamic)browser).GoBack();

    public static void Stop(object browser) => ((dynamic)browser).Stop();

    /// <summary>
    /// Opens <paramref name="url"/> through the tab's own Shell object (its folder view's Application, which lives in explorer.exe), so
    /// the browser is started by Explorer: the foreground program while someone searches in it, which Windows lets bring the browser to
    /// the front. A background program such as the agent can't.
    /// </summary>
    public static void ShellExecute(object browser, string url)
    {
        dynamic window = browser;
        object? document = null, application = null;
        try
        {
            document = window.Document;
            application = ((dynamic)document!).Application;
            ((dynamic)application!).ShellExecute(url, "", "", "open", 1); // SW_SHOWNORMAL
        }
        finally
        {
            Release(application);
            Release(document);
        }
    }

    /// <summary>Connects <paramref name="sink"/> to an object's events (connection point <typeparamref name="T"/>); dispose to disconnect.</summary>
    public static IDisposable Connect<T>(object source, T sink) where T : class
    {
        var container = (IConnectionPointContainer)source;
        var id = typeof(T).GUID;
        container.FindConnectionPoint(ref id, out var point);
        if (point is null)
        {
            throw new COMException($"no connection point {typeof(T).Name}");
        }
        try
        {
            point.Advise(sink, out var cookie);
            return new Connection(point, cookie);
        }
        catch
        {
            Release(point);
            throw;
        }
    }

    /// <summary>An advised sink: disposing unadvises it and releases the connection point.</summary>
    sealed class Connection(IConnectionPoint point, int cookie) : IDisposable
    {
        int done;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref done, 1) == 0)
            {
                try
                {
                    point.Unadvise(cookie);
                }
                catch (Exception e) when (e is COMException or InvalidComObjectException)
                {
                }
                finally
                {
                    Release(point);
                }
            }
        }
    }

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IServiceProvider
    {
        [PreserveSig]
        int QueryService(ref Guid service, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object? result);
    }

    /// <summary>IShellBrowser up to QueryActiveShellView, in vtable order (IOleWindow first).</summary>
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellBrowser
    {
        void GetWindow(out IntPtr hwnd);
        void ContextSensitiveHelp(int enterMode);
        void InsertMenusSB(IntPtr shared, IntPtr widths);
        void SetMenuSB(IntPtr shared, IntPtr oleMenu, IntPtr activeObject);
        void RemoveMenusSB(IntPtr shared);
        void SetStatusTextSB(IntPtr text);
        void EnableModelessSB(int enable);
        void TranslateAcceleratorSB(IntPtr message, ushort id);
        void BrowseObject(IntPtr pidl, uint flags);
        void GetViewStateStream(uint mode, out IntPtr stream);
        void GetControlWindow(uint id, out IntPtr hwnd);
        void SendControlMsg(uint id, uint message, IntPtr wParam, IntPtr lParam, out IntPtr result);
        void QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object view);
    }

    /// <summary>IFolderView up to GetFolder.</summary>
    [ComImport, Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IFolderView
    {
        void GetCurrentViewMode(out uint mode);
        void SetCurrentViewMode(uint mode);
        void GetFolder(ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object folder);
    }

    [ComImport, Guid("1AC3D9F0-175C-11D1-95BE-00609797EA4F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPersistFolder2
    {
        void GetClassID(out Guid clsid);
        void Initialize(IntPtr pidl);
        void GetCurFolder(out IntPtr pidl);
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetNameFromIDList(IntPtr pidl, uint sigdn, out IntPtr name);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(IntPtr hwnd, [Out] char[] name, int length);
}

/// <summary>Shell Windows events: a window or tab was added or closed.</summary>
[ComImport, Guid("FE4106E0-399A-11D0-A48C-00A0C90A8F39"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DShellWindowsEvents
{
    [DispId(200)]
    void WindowRegistered(int cookie);

    [DispId(201)]
    void WindowRevoked(int cookie);
}

/// <summary>A window's navigation events (the part VFS365 listens to).</summary>
[ComImport, Guid("34A715A0-6587-11D0-924A-0020AFC7AC4D"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
public interface DWebBrowserEvents2
{
    [DispId(250)]
    void BeforeNavigate2([In, MarshalAs(UnmanagedType.IDispatch)] object browser, [In] ref object url, [In] ref object flags, [In] ref object frame,
        [In] ref object postData, [In] ref object headers, [In, Out] ref bool cancel);

    [DispId(252)]
    void NavigateComplete2([In, MarshalAs(UnmanagedType.IDispatch)] object browser, [In] ref object url);

    [DispId(259)]
    void DocumentComplete([In, MarshalAs(UnmanagedType.IDispatch)] object browser, [In] ref object url);
}
