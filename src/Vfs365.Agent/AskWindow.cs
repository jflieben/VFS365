using System.Runtime.InteropServices;
using System.Text;

namespace Vfs365.Agent;

/// <summary>
/// The agent's small windows: "Search in Microsoft 365", which asks for words, and a question with two choices (an Office type without
/// an app). Dialogs from a template in memory (user32 only; the dialog manager gives Tab, Enter, Escape and scaling per monitor), with
/// Windows' visual styles from shell32's manifest when they load. One at a time: a request while one is open brings that one forward and
/// gets nothing.
/// </summary>
internal static unsafe partial class AskWindow
{
    internal const int IdOk = 1, IdCancel = 2, IdEdit = 100, IdPrompt = 101, IdSecond = 102;
    const uint WmInitDialog = 0x0110, WmCommand = 0x0111, WmClose = 0x0010, EmLimitText = 0x00C5;
    static readonly Lock gate = new();
    static IntPtr shown;
    static string prompt = "";
    static string? answer;

    /// <summary>Asks for search words below <paramref name="text"/>; null when cancelled, closed or another one is open.</summary>
    public static Task<string?> AskAsync(string text, CancellationToken ct) => RunAsync<string?>(text, ShowSearch, null, "VFS365 search words", ct);

    /// <summary>
    /// Asks <paramref name="text"/>: 1 for <paramref name="first"/> (the default button), 2 for <paramref name="second"/>, 0 when cancelled,
    /// closed or another one is open.
    /// </summary>
    public static Task<int> ChooseAsync(string title, string text, string first, string second, string cancel, CancellationToken ct) =>
        RunAsync(text, () => ShowChoice(title, first, second, cancel), 0, "VFS365 question", ct);

    static Task<T> RunAsync<T>(string text, Func<T> show, T none, string name, CancellationToken ct)
    {
        lock (gate)
        {
            if (shown != IntPtr.Zero || prompt.Length > 0)
            {
                if (shown != IntPtr.Zero)
                {
                    SetForegroundWindow(shown);
                }
                return Task.FromResult(none);
            }
            prompt = text;
        }
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var value = none;
            try
            {
                using var closing = ct.Register(() =>
                {
                    lock (gate)
                    {
                        if (shown != IntPtr.Zero)
                        {
                            PostMessageW(shown, WmClose, 0, 0);
                        }
                    }
                });
                value = show();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                value = none;
            }
            finally
            {
                lock (gate)
                {
                    shown = IntPtr.Zero;
                    prompt = "";
                }
                result.TrySetResult(value);
            }
        })
        { IsBackground = true, Name = name };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    static string? ShowSearch()
    {
        answer = null;
        return Run(Template(UiText.Get("Search.Title"), UiText.Get("Search.Button"), UiText.Get("Search.Cancel")), &DialogProc) == 1 ? answer : null;
    }

    static int ShowChoice(string title, string first, string second, string cancel) =>
        Run(ChoiceTemplate(title, first, second, cancel), &ChoiceProc) switch { 1 => 1, 3 => 2, _ => 0 };

    /// <summary>Shows a dialog from <paramref name="template"/> until it ends; what EndDialog gave.</summary>
    static nint Run(byte[] template, delegate* unmanaged<nint, uint, nint, nint, nint> proc)
    {
        _ = SetThreadDpiAwarenessContext(-4); // per monitor v2: the dialog manager scales it
        var styles = VisualStyles(out var cookie);
        var memory = Marshal.AllocHGlobal(template.Length);
        try
        {
            Marshal.Copy(template, 0, memory, template.Length);
            return DialogBoxIndirectParamW(GetModuleHandleW(null), memory, IntPtr.Zero, proc, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
            if (styles != IntPtr.Zero)
            {
                DeactivateActCtx(0, cookie);
                ReleaseActCtx(styles);
            }
        }
    }

    [UnmanagedCallersOnly]
    static nint DialogProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmInitDialog:
                    lock (gate)
                    {
                        shown = window;
                    }
                    SetDlgItemTextW(window, IdPrompt, prompt);
                    SendDlgItemMessageW(window, IdEdit, EmLimitText, SearchTerms.MaxLength, 0);
                    Present(window);
                    SetFocus(GetDlgItem(window, IdEdit));
                    return 0; // focus set here
                case WmCommand:
                    switch ((int)(wParam & 0xFFFF))
                    {
                        case IdOk:
                            var buffer = stackalloc char[SearchTerms.MaxLength + 2];
                            var length = GetDlgItemTextW(window, IdEdit, buffer, SearchTerms.MaxLength + 1);
                            var text = new string(buffer, 0, Math.Max(0, length)).Trim();
                            if (text.Length == 0)
                            {
                                MessageBeep(0);
                                return 1;
                            }
                            answer = text;
                            EndDialog(window, 1);
                            return 1;
                        case IdCancel:
                            EndDialog(window, 0);
                            return 1;
                    }
                    return 0;
            }
        }
        catch (Exception)
        {
            // Never into the dialog manager
        }
        return 0;
    }

    /// <summary>The question: the first choice ends it with 1, the second with 3, Cancel and Escape with 0.</summary>
    [UnmanagedCallersOnly]
    static nint ChoiceProc(nint window, uint message, nint wParam, nint lParam)
    {
        try
        {
            switch (message)
            {
                case WmInitDialog:
                    lock (gate)
                    {
                        shown = window;
                    }
                    SetDlgItemTextW(window, IdPrompt, prompt);
                    Present(window);
                    SetFocus(GetDlgItem(window, IdOk));
                    return 0; // focus set here
                case WmCommand:
                    switch ((int)(wParam & 0xFFFF))
                    {
                        case IdOk:
                            EndDialog(window, 1);
                            return 1;
                        case IdSecond:
                            EndDialog(window, 3);
                            return 1;
                        case IdCancel:
                            EndDialog(window, 0);
                            return 1;
                    }
                    return 0;
            }
        }
        catch (Exception)
        {
            // Never into the dialog manager
        }
        return 0;
    }

    /// <summary>Placed, and to the front; it flashes when Windows doesn't allow that.</summary>
    static void Present(IntPtr window)
    {
        Place(window);
        if (!SetForegroundWindow(window))
        {
            var flash = new FlashInfo { Size = (uint)sizeof(FlashInfo), Window = window, Flags = 3 | 12, Count = 3 }; // FLASHW_ALL | FLASHW_TIMERNOFG
            FlashWindowEx(ref flash);
        }
    }

    /// <summary>Centred on the work area of the monitor under the cursor, in its upper third.</summary>
    static void Place(IntPtr window)
    {
        if (!GetCursorPos(out var cursor) || !GetWindowRect(window, out var frame))
        {
            return;
        }
        var info = new MonitorInfo { Size = (uint)sizeof(MonitorInfo) };
        if (!GetMonitorInfoW(MonitorFromPoint(cursor, 2), ref info)) // MONITOR_DEFAULTTONEAREST
        {
            return;
        }
        var work = info.Work;
        var width = frame.Right - frame.Left;
        var height = frame.Bottom - frame.Top;
        var x = work.Left + Math.Max(0, (work.Right - work.Left - width) / 2);
        var y = work.Top + Math.Max(0, (work.Bottom - work.Top - height) / 3);
        SetWindowPos(window, IntPtr.Zero, x, y, 0, 0, 0x0001 | 0x0004); // SWP_NOSIZE | SWP_NOZORDER
    }

    /// <summary>
    /// For tests: the class names of the controls <paramref name="ids"/> of the dialog made hidden from <paramref name="template"/>
    /// (default: <see cref="Template"/>'s), destroyed at once.
    /// </summary>
    internal static string[] HiddenControls(byte[]? template = null, int[]? ids = null)
    {
        template ??= Template("Search", "OK", "Cancel");
        var memory = Marshal.AllocHGlobal(template.Length);
        try
        {
            Marshal.Copy(template, 0, memory, template.Length);
            var dialog = CreateDialogIndirectParamW(GetModuleHandleW(null), memory, IntPtr.Zero, &QuietProc, 0);
            if (dialog == IntPtr.Zero)
            {
                return [];
            }
            try
            {
                var name = stackalloc char[64];
                var names = new List<string>();
                foreach (var id in ids ?? [IdPrompt, IdEdit, IdOk, IdCancel])
                {
                    names.Add(new string(name, 0, Math.Max(0, GetClassNameW(GetDlgItem(dialog, id), name, 64))));
                }
                return [.. names];
            }
            finally
            {
                DestroyWindow(dialog);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    [UnmanagedCallersOnly]
    static nint QuietProc(nint window, uint message, nint wParam, nint lParam) => 0;

    const uint TabStop = 0x00010000, Border = 0x00800000, AutoHScroll = 0x80, DefPush = 0x01, NoPrefix = 0x80;
    const ushort Button = 0x0080, Edit = 0x0081, Static = 0x0082;

    readonly record struct Control(uint Style, short X, short Y, short Width, short Height, int Id, ushort Atom, string Text);

    /// <summary>The search template: prompt, edit field, Search and Cancel, in dialog units with Segoe UI 9.</summary>
    internal static byte[] Template(string title, string ok, string cancel) => Build(title, 62,
        new(0, 7, 7, 246, 10, IdPrompt, Static, ""),
        new(Border | TabStop | AutoHScroll, 7, 20, 246, 14, IdEdit, Edit, ""),
        new(DefPush | TabStop, 149, 41, 50, 14, IdOk, Button, ok),
        new(TabStop, 203, 41, 50, 14, IdCancel, Button, cancel));

    /// <summary>The question template: up to four lines of text (a file name's &amp; shows as is), two choices and Cancel.</summary>
    internal static byte[] ChoiceTemplate(string title, string first, string second, string cancel) => Build(title, 69,
        new(NoPrefix, 7, 7, 246, 34, IdPrompt, Static, ""),
        new(DefPush | TabStop, 35, 48, 80, 14, IdOk, Button, first),
        new(TabStop, 119, 48, 80, 14, IdSecond, Button, second),
        new(TabStop, 203, 48, 50, 14, IdCancel, Button, cancel));

    /// <summary>A dialog template 260 dialog units wide with <paramref name="controls"/>.</summary>
    static byte[] Build(string title, short height, params Control[] controls)
    {
        const uint popup = 0x80000000, visible = 0x10000000, child = 0x40000000, caption = 0x00C00000, sysMenu = 0x00080000;
        const uint setFont = 0x40, modalFrame = 0x80;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode);
        void Text(string value)
        {
            writer.Write(Encoding.Unicode.GetBytes(value));
            writer.Write((ushort)0);
        }
        void Align()
        {
            while (stream.Position % 4 != 0)
            {
                writer.Write((byte)0);
            }
        }
        writer.Write(popup | caption | sysMenu | setFont | modalFrame);
        writer.Write(0x00040000u); // WS_EX_APPWINDOW: a taskbar button, so it can't get lost behind Explorer
        writer.Write((ushort)controls.Length);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)260);
        writer.Write(height);
        writer.Write((ushort)0); // no menu
        writer.Write((ushort)0); // standard dialog class
        Text(title);
        writer.Write((ushort)9);
        Text("Segoe UI");
        foreach (var control in controls)
        {
            Align();
            writer.Write(control.Style | child | visible);
            writer.Write(0u);
            writer.Write(control.X);
            writer.Write(control.Y);
            writer.Write(control.Width);
            writer.Write(control.Height);
            writer.Write((ushort)control.Id);
            writer.Write((ushort)0xFFFF);
            writer.Write(control.Atom); // predefined class: BUTTON, EDIT, STATIC
            Text(control.Text);
            writer.Write((ushort)0); // no creation data
        }
        return stream.ToArray();
    }

    /// <summary>Activates Windows' common controls 6 (visual styles) from shell32's manifest; zero when that fails (classic look).</summary>
    static IntPtr VisualStyles(out IntPtr cookie)
    {
        cookie = IntPtr.Zero;
        var source = Marshal.StringToHGlobalUni(Path.Combine(Environment.SystemDirectory, "shell32.dll"));
        try
        {
            var context = new ActCtx { Size = (uint)sizeof(ActCtx), Flags = 0x08, Source = source, ResourceName = 124 }; // ACTCTX_FLAG_RESOURCE_NAME_VALID
            var handle = CreateActCtxW(ref context);
            if (handle == -1)
            {
                return IntPtr.Zero;
            }
            if (!ActivateActCtx(handle, out cookie))
            {
                ReleaseActCtx(handle);
                return IntPtr.Zero;
            }
            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(source);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ActCtx
    {
        public uint Size, Flags;
        public IntPtr Source;
        public ushort Architecture, LangId;
        public IntPtr AssemblyDirectory, ResourceName, ApplicationName, Module;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor, Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags, Count, Timeout;
    }

    [LibraryImport("user32.dll")]
    private static partial nint DialogBoxIndirectParamW(IntPtr instance, IntPtr template, IntPtr parent, delegate* unmanaged<nint, uint, nint, nint, nint> proc, nint init);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CreateDialogIndirectParamW(IntPtr instance, IntPtr template, IntPtr parent, delegate* unmanaged<nint, uint, nint, nint, nint> proc, nint init);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    private static partial int GetClassNameW(IntPtr window, char* name, int capacity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EndDialog(IntPtr dialog, nint result);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDlgItemTextW(IntPtr dialog, int id, string text);

    [LibraryImport("user32.dll")]
    private static partial int GetDlgItemTextW(IntPtr dialog, int id, char* text, int capacity);

    [LibraryImport("user32.dll")]
    private static partial nint SendDlgItemMessageW(IntPtr dialog, int id, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDlgItem(IntPtr dialog, int id);

    [LibraryImport("user32.dll")]
    private static partial IntPtr SetFocus(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(IntPtr window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr window, out Rect rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandleW(string? name);

    [LibraryImport("kernel32.dll")]
    private static partial nint CreateActCtxW(ref ActCtx context);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ActivateActCtx(IntPtr context, out IntPtr cookie);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeactivateActCtx(uint flags, IntPtr cookie);

    [LibraryImport("kernel32.dll")]
    private static partial void ReleaseActCtx(IntPtr context);
}
