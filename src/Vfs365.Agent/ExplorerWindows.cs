using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace Vfs365.Agent;

/// <summary>
/// Explorer's open windows and tabs through the Shell Windows object (COM, served by explorer.exe in this session): where each one is,
/// and navigating one elsewhere. IWebBrowser2 is late bound (IDispatch by name), so no vtable or VARIANT layout differs between x64 and
/// ARM64; the parsing name comes from the window's shell view. Callers catch COMException (Explorer restarting) and others.
/// </summary>
internal static partial class ExplorerWindows
{
    static readonly Guid ShellWindowsClsid = new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");
    static readonly Guid TopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
    const uint DesktopAbsoluteParsing = 0x80028000; // SIGDN_DESKTOPABSOLUTEPARSING

    /// <summary>
    /// A window or tab. Browser: its IWebBrowser2 (for <see cref="Navigate"/>). Identity: its COM identity in this process, which tells
    /// tabs of one window apart. ParsingName: the shown folder's parsing name (a search: see <see cref="ExplorerSearch.FromView"/>).
    /// </summary>
    public sealed record Window(object Browser, long Identity, IntPtr Hwnd, uint ProcessId, uint SessionId, string ClassName, string? LocationUrl,
        string? LocationName, string? ParsingName);

    public static object Open() =>
        Activator.CreateInstance(Type.GetTypeFromCLSID(ShellWindowsClsid, throwOnError: true)!) ?? throw new COMException("no Shell Windows object");

    /// <summary>The windows and tabs, at most <paramref name="max"/>; entries that fail on their own are left out (problem says why).</summary>
    public static List<Window> List(object shellWindows, int max, Action<string>? problem = null)
    {
        dynamic windows = shellWindows;
        var count = Math.Min((int)windows.Count, max);
        var list = new List<Window>(count);
        for (var i = 0; i < count; i++)
        {
            try
            {
                object? browser = windows.Item(i);
                if (browser is not null)
                {
                    list.Add(Describe(browser));
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
        dynamic window = browser;
        var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        _ = ProcessIdToSessionId(pid, out var session);
        var classBuffer = new char[64];
        var className = new string(classBuffer, 0, Math.Max(0, GetClassNameW(hwnd, classBuffer, classBuffer.Length)));
        string? url = null, name = null;
        try
        {
            url = window.LocationURL;
            name = window.LocationName;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
        }
        var unknown = Marshal.GetIUnknownForObject(browser);
        Marshal.Release(unknown);
        return new Window(browser, unknown.ToInt64(), hwnd, pid, session, className, url, name, ParsingName(browser));
    }

    /// <summary>The shown folder's parsing name: window, top-level browser, active shell view, its folder, the folder's ID list.</summary>
    public static string? ParsingName(object browser)
    {
        var pidl = IntPtr.Zero;
        try
        {
            var service = TopLevelBrowser;
            var shellBrowserId = typeof(IShellBrowser).GUID;
            if (browser is not IServiceProvider provider || provider.QueryService(ref service, ref shellBrowserId, out var found) != 0
                || found is not IShellBrowser shellBrowser)
            {
                return null;
            }
            shellBrowser.QueryActiveShellView(out var view);
            if (view is not IFolderView folderView)
            {
                return null;
            }
            var persistId = typeof(IPersistFolder2).GUID;
            folderView.GetFolder(ref persistId, out var folder);
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
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
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
        dynamic shell = window.Document.Application;
        shell.ShellExecute(url, "", "", "open", 1); // SW_SHOWNORMAL
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
        point.Advise(sink, out var cookie);
        return new Connection(point, cookie);
    }

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
                catch (COMException)
                {
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
