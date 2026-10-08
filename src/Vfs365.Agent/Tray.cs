using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Vfs365.Agent;

public enum TrayState { Normal, Waiting, Error }

/// <summary>What the tray menu opens and does. RefreshBlocked: null when Refresh is allowed now, else why not (its tooltip).</summary>
public sealed record TrayActions(string ShowFiles, string HelpUrl, Action Restart, Action Refresh, Func<string?> RefreshBlocked);

/// <summary>
/// Tray icon of the background agent: a single-colour glyph that follows the taskbar theme (assets\tray, tools/New-TrayIcons.ps1)
/// with a badge for uploads waiting or a sign-in problem, a status tooltip, a menu, and notifications (shown as toasts on Windows 10
/// and 11 with the VFS365 icon). Plain Win32 on its own DPI-aware thread, no UI framework. Survives an Explorer restart.
/// </summary>
sealed class Tray : IDisposable
{
    const uint CallbackMessage = 0x0400 + 1, WmLButtonUp = 0x0202, WmRButtonUp = 0x0205, WmClose = 0x0010, WmDestroy = 0x0002, WmSettingChange = 0x001A;
    const uint NimAdd = 0, NimModify = 1, NimDelete = 2, NifMessage = 1, NifIcon = 2, NifTip = 4, NifInfo = 0x10;
    const uint NiifUser = 4, NiifLargeIcon = 0x20, MfString = 0, MfGrayed = 1, MfSeparator = 0x800, TpmReturnCmd = 0x100, TpmRightButton = 2;
    const int ShowFiles = 1, ShowLog = 2, Restart = 3, Help = 4, Website = 5, Refresh = 6;
    public const string JSolveUrl = "https://jsolve.nl";

    readonly string label;
    readonly TrayActions actions;
    readonly Action<string> log;
    readonly WndProc procedure;
    readonly Thread thread;
    readonly ManualResetEventSlim ready = new();
    readonly string iconFolder = Path.Combine(AppContext.BaseDirectory, "tray");
    IntPtr window, icon, balloonIcon;
    uint taskbarCreated;
    volatile string status;
    TrayState state = TrayState.Normal;

    /// <summary>The hint window shown while the pointer is on a greyed-out Refresh, and the open menu with Refresh's position in it.</summary>
    IntPtr tooltip, openMenu;
    int refreshPosition = -1;
    string? refreshBlocked;

    public Tray(string label, TrayActions actions, Action<string> log)
    {
        (this.label, this.actions, this.log) = (label, actions, log);
        status = "starting";
        procedure = WindowProcedure;
        thread = new Thread(Run) { IsBackground = true, Name = "VFS365 tray" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>The status (for example "up to date"): the icon's tooltip says "VFS365: up to date", the menu "Status: up to date"; and the badge.</summary>
    public void SetStatus(string text, TrayState newState)
    {
        if (text == status && newState == state)
        {
            return;
        }
        status = text;
        if (newState != state)
        {
            state = newState;
            PostMessage(window, ReloadIconMessage, IntPtr.Zero, IntPtr.Zero);
        }
        var data = Data(NifTip);
        Shell_NotifyIcon(NimModify, ref data);
    }

    public void Notify(string title, string text)
    {
        var data = Data(NifInfo);
        data.szInfoTitle = Trim(title, 63);
        data.szInfo = Trim(text, 255);
        data.dwInfoFlags = NiifUser | NiifLargeIcon;
        data.hBalloonIcon = balloonIcon;
        Shell_NotifyIcon(NimModify, ref data);
    }

    const uint ReloadIconMessage = 0x0400 + 2, WmMenuSelect = 0x011F, WmExitMenuLoop = 0x0212;

    void Run()
    {
        try
        {
            // Per-monitor DPI awareness for this thread only: the icon is loaded at the taskbar's real size instead of scaled up from 16 px
            SetThreadDpiAwarenessContext(new IntPtr(-4));
            AllowDarkMenus();
            var instance = GetModuleHandle(null);
            var windowClass = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
                hInstance = instance,
                lpszClassName = "VFS365.Tray",
            };
            RegisterClassEx(ref windowClass);
            window = CreateWindowEx(0, "VFS365.Tray", label, 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            taskbarCreated = RegisterWindowMessage("TaskbarCreated");
            ExtractIconEx(Environment.ProcessPath!, 0, out balloonIcon, IntPtr.Zero, 1);
            LoadIcon();
            Add();
            CreateTooltip();
        }
        catch (Exception e)
        {
            log($"tray: {e.Message}");
        }
        finally
        {
            ready.Set();
        }
        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
    }

    /// <summary>The glyph for the taskbar theme and state, at the notification area's size for the current DPI.</summary>
    void LoadIcon()
    {
        var theme = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1 ? "light" : "dark";
        var file = Path.Combine(iconFolder, $"tray-{theme}-{state.ToString().ToLowerInvariant()}.ico");
        var size = GetSystemMetricsForDpi(49, GetDpiForSystem()); // SM_CXSMICON
        var loaded = File.Exists(file) ? LoadImage(IntPtr.Zero, file, 1, size, size, 0x10) : IntPtr.Zero; // IMAGE_ICON, LR_LOADFROMFILE
        if (loaded == IntPtr.Zero)
        {
            ExtractIconEx(Environment.ProcessPath!, 0, IntPtr.Zero, out loaded, 1);
        }
        var old = icon;
        icon = loaded;
        if (old != IntPtr.Zero)
        {
            var data = Data(NifIcon);
            Shell_NotifyIcon(NimModify, ref data);
            DestroyIcon(old);
        }
    }

    IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (hwnd != window && window != IntPtr.Zero)
            {
                // The hint window: it only paints itself
                if (message == WmPaint)
                {
                    PaintHint(hwnd);
                    return IntPtr.Zero;
                }
                return DefWindowProc(hwnd, message, wParam, lParam);
            }
            if (message == CallbackMessage)
            {
                switch ((uint)lParam.ToInt64() & 0xFFFF)
                {
                    case WmLButtonUp:
                        Open(actions.ShowFiles);
                        break;
                    case WmRButtonUp:
                        ShowMenu();
                        break;
                }
                return IntPtr.Zero;
            }
            if (message == ReloadIconMessage)
            {
                LoadIcon();
                return IntPtr.Zero;
            }
            if (message == WmMenuSelect)
            {
                // Sent for disabled items too: explain a greyed-out Refresh while the pointer or keyboard is on it
                var item = (int)(wParam.ToInt64() & 0xFFFF);
                if (item == Refresh && refreshBlocked is not null && openMenu != IntPtr.Zero && lParam == openMenu)
                {
                    ShowTooltip(refreshBlocked);
                }
                else
                {
                    HideTooltip();
                }
                return IntPtr.Zero;
            }
            if (message == WmExitMenuLoop)
            {
                HideTooltip();
                return IntPtr.Zero;
            }
            if (message == WmSettingChange && lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
            {
                // Light or dark taskbar switched
                LoadIcon();
                return IntPtr.Zero;
            }
            if (message == taskbarCreated && taskbarCreated != 0)
            {
                Add();
                return IntPtr.Zero;
            }
            if (message == WmClose)
            {
                var data = Data(0);
                Shell_NotifyIcon(NimDelete, ref data);
                DestroyWindow(hwnd);
                return IntPtr.Zero;
            }
            if (message == WmDestroy)
            {
                PostQuitMessage(0);
                return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            log($"tray: {e.Message}");
        }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    void Add()
    {
        var data = Data(NifMessage | NifIcon | NifTip);
        if (!Shell_NotifyIcon(NimAdd, ref data))
        {
            log("tray: Windows did not add the icon");
        }
    }

    void ShowMenu()
    {
        refreshBlocked = actions.RefreshBlocked();
        var menu = CreatePopupMenu();
        AppendMenu(menu, MfString | MfGrayed, 0, $"Status: {status}");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, ShowFiles, "Show files");
        AppendMenu(menu, MfString, ShowLog, "Show log");
        refreshPosition = 4; // status, separator, Show files, Show log
        AppendMenu(menu, MfString | (refreshBlocked is null ? 0 : MfGrayed), Refresh, "Refresh");
        AppendMenu(menu, MfString, Restart, "Restart");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, Help, "Help");
        AppendMenu(menu, MfSeparator, 0, null);
        AppendMenu(menu, MfString, Website, $"VFS365 {typeof(Tray).Assembly.GetName().Version!.ToString(3)} by JSolve B.V.");
        SetMenuDefaultItem(menu, ShowFiles, 0);
        GetCursorPos(out var point);
        SetForegroundWindow(window);
        openMenu = menu;
        var command = TrackPopupMenu(menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, 0, window, IntPtr.Zero);
        openMenu = IntPtr.Zero;
        HideTooltip();
        DestroyMenu(menu);
        switch (command)
        {
            case ShowFiles:
                Open(actions.ShowFiles);
                break;
            case ShowLog:
                Start("notepad.exe", $"\"{AgentLog.FilePath}\"");
                break;
            case Refresh:
                actions.Refresh();
                break;
            case Restart:
                actions.Restart();
                break;
            case Help:
                Open(actions.HelpUrl);
                break;
            case Website:
                Open(JSolveUrl);
                break;
        }
    }

    // The hint beside a greyed-out Refresh. Standard menus have no tooltips, and the common controls tooltip crashed in this process
    // (comctl32 5.82 on ARM64), so this is a plain popup window painted with user32 and gdi32 only.
    const string HintClass = "VFS365.Hint";
    const int HintPadding = 6;
    IntPtr hintFont;
    string hintText = "";

    void CreateTooltip()
    {
        var hintClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(procedure),
            hInstance = GetModuleHandle(null),
            lpszClassName = HintClass,
        };
        RegisterClassEx(ref hintClass);
        // WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, WS_POPUP | WS_BORDER
        tooltip = CreateWindowEx(0x8 | 0x80 | 0x08000000, HintClass, "", 0x80000000 | 0x00800000, 0, 0, 0, 0, window, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        hintFont = CreateFont(-MulDiv(9, (int)GetDpiForSystem(), 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI"); // DEFAULT_CHARSET, CLEARTYPE_QUALITY
    }

    /// <summary>Shows <paramref name="text"/> beside the Refresh item, to the left of the menu (the tray sits at the screen's edge), else to its right.</summary>
    void ShowTooltip(string text)
    {
        if (tooltip == IntPtr.Zero || hintFont == IntPtr.Zero || !GetMenuItemRect(window, openMenu, (uint)refreshPosition, out var item))
        {
            return;
        }
        hintText = text;
        var bounds = new RECT { Right = MulDiv(320, (int)GetDpiForSystem(), 96) };
        var dc = GetDC(tooltip);
        var previous = SelectObject(dc, hintFont);
        DrawText(dc, hintText, -1, ref bounds, DtWordBreak | DtNoPrefix | DtCalcRect);
        SelectObject(dc, previous);
        ReleaseDC(tooltip, dc);
        var width = bounds.Right + 2 * HintPadding + 2;
        var height = bounds.Bottom + 2 * HintPadding + 2;

        var monitor = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromRect(ref item, 2), ref monitor); // MONITOR_DEFAULTTONEAREST
        var x = item.Left - width - 4 >= monitor.rcWork.Left ? item.Left - width - 4 : item.Right + 4;
        var y = Math.Min(item.Top, monitor.rcWork.Bottom - height);
        SetWindowPos(tooltip, new IntPtr(-1), x, y, width, height, 0x10 | 0x40); // HWND_TOPMOST, SWP_NOACTIVATE | SWP_SHOWWINDOW
        InvalidateRect(tooltip, IntPtr.Zero, true);
    }

    void HideTooltip()
    {
        if (tooltip != IntPtr.Zero)
        {
            ShowWindow(tooltip, 0);
        }
    }

    void PaintHint(IntPtr hwnd)
    {
        GetClientRect(hwnd, out var client);
        var dc = GetDC(hwnd);
        FillRect(dc, ref client, GetSysColorBrush(24)); // COLOR_INFOBK
        SetBkMode(dc, 1); // TRANSPARENT
        SetTextColor(dc, GetSysColor(23)); // COLOR_INFOTEXT
        var previous = SelectObject(dc, hintFont);
        var text = new RECT { Left = HintPadding, Top = HintPadding, Right = client.Right - HintPadding, Bottom = client.Bottom - HintPadding };
        DrawText(dc, hintText, -1, ref text, DtWordBreak | DtNoPrefix);
        SelectObject(dc, previous);
        ReleaseDC(hwnd, dc);
        ValidateRect(hwnd, IntPtr.Zero);
    }

    const uint DtWordBreak = 0x10, DtCalcRect = 0x400, DtNoPrefix = 0x800, WmPaint = 0x000F;

    /// <summary>A shell location (shell:::{CLSID}, path) in Explorer, or a URL in the default browser.</summary>
    void Open(string target)
    {
        if (target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            Start(target, null);
        }
        else
        {
            Start("explorer.exe", target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) ? target : $"\"{target}\"");
        }
    }

    void Start(string program, string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(program) { Arguments = arguments ?? "", UseShellExecute = true })?.Dispose();
        }
        catch (Exception e)
        {
            log($"tray: {e.Message}");
        }
    }

    NOTIFYICONDATA Data(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = window,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = Trim($"{label}: {status}", 127),
        szInfo = "",
        szInfoTitle = "",
    };

    static string Trim(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    /// <summary>Menus follow the system's dark mode. Undocumented uxtheme ordinals (Windows 10 1903 and later), as many tray apps use; ignored when missing.</summary>
    static void AllowDarkMenus()
    {
        try
        {
            var uxtheme = NativeLibrary.Load("uxtheme.dll");
            var setPreferredAppMode = GetProcAddress(uxtheme, new IntPtr(135));
            var flushMenuThemes = GetProcAddress(uxtheme, new IntPtr(136));
            if (setPreferredAppMode != IntPtr.Zero && flushMenuThemes != IntPtr.Zero)
            {
                Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeFn>(setPreferredAppMode)(1); // AllowDark
                Marshal.GetDelegateForFunctionPointer<FlushMenuThemesFn>(flushMenuThemes)();
            }
        }
        catch (Exception)
        {
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int SetPreferredAppModeFn(int mode);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate void FlushMenuThemesFn();

    public void Dispose()
    {
        if (window != IntPtr.Zero)
        {
            PostMessage(window, WmClose, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
        }
        foreach (var handle in new[] { icon, balloonIcon })
        {
            if (handle != IntPtr.Zero)
            {
                DestroyIcon(handle);
            }
        }
        if (hintFont != IntPtr.Zero)
        {
            DeleteObject(hintFont);
        }
        ready.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int DrawText(IntPtr dc, string text, int length, ref RECT rect, uint format);

    [DllImport("user32.dll")]
    static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);

    [DllImport("user32.dll")]
    static extern IntPtr GetSysColorBrush(int index);

    [DllImport("user32.dll")]
    static extern uint GetSysColor(int index);

    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    static extern bool ValidateRect(IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFont(int height, int width, int escapement, int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outputPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string face);

    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    static extern int SetBkMode(IntPtr dc, int mode);

    [DllImport("gdi32.dll")]
    static extern uint SetTextColor(IntPtr dc, uint color);

    [DllImport("user32.dll")]
    static extern bool GetMenuItemRect(IntPtr hwnd, IntPtr menu, uint item, out RECT rect);

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll")]
    static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("kernel32.dll")]
    static extern int MulDiv(int number, int numerator, int denominator);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, out IntPtr large, IntPtr small, uint icons);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr large, out IntPtr small, uint icons);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr instance, string name, uint type, int width, int height, uint flags);

    [DllImport("user32.dll")]
    static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    static extern uint GetDpiForSystem();

    [DllImport("user32.dll")]
    static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? module);

    [DllImport("kernel32.dll")]
    static extern IntPtr GetProcAddress(IntPtr module, IntPtr ordinal);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateWindowEx(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetMessage(out MSG message, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? text);

    [DllImport("user32.dll")]
    static extern bool SetMenuDefaultItem(IntPtr menu, int item, int byPosition);

    [DllImport("user32.dll")]
    static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr icon);
}
