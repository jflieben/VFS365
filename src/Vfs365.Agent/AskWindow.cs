using System.Runtime.InteropServices;
using System.Text;

namespace Vfs365.Agent;

/// <summary>
/// The "Search in Microsoft 365" window that asks for words: a dialog from a template in memory (user32 only; the dialog manager gives
/// Tab, Enter, Escape and scaling per monitor), with Windows' visual styles from shell32's manifest when they load. One at a time: a
/// request while one is open brings that one forward and gets null.
/// </summary>
internal static unsafe partial class AskWindow
{
    const int IdOk = 1, IdCancel = 2, IdEdit = 100, IdPrompt = 101;
    const uint WmInitDialog = 0x0110, WmCommand = 0x0111, WmClose = 0x0010, EmLimitText = 0x00C5;
    static readonly Lock gate = new();
    static IntPtr shown;
    static string prompt = "";
    static string? answer;

    /// <summary>Asks for search words below <paramref name="text"/>; null when cancelled, closed or another one is open.</summary>
    public static Task<string?> AskAsync(string text, CancellationToken ct)
    {
        lock (gate)
        {
            if (shown != IntPtr.Zero || prompt.Length > 0)
            {
                if (shown != IntPtr.Zero)
                {
                    SetForegroundWindow(shown);
                }
                return Task.FromResult<string?>(null);
            }
            prompt = text;
        }
        var result = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            string? words = null;
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
                words = Show();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                words = null;
            }
            finally
            {
                lock (gate)
                {
                    shown = IntPtr.Zero;
                    prompt = "";
                }
                result.TrySetResult(words);
            }
        })
        { IsBackground = true, Name = "VFS365 search words" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return result.Task;
    }

    static string? Show()
    {
        _ = SetThreadDpiAwarenessContext(-4); // per monitor v2: the dialog manager scales it
        var styles = VisualStyles(out var cookie);
        var template = Template(UiText.Get("Search.Title"), UiText.Get("Search.Button"), UiText.Get("Search.Cancel"));
        var memory = Marshal.AllocHGlobal(template.Length);
        try
        {
            Marshal.Copy(template, 0, memory, template.Length);
            answer = null;
            var ended = DialogBoxIndirectParamW(GetModuleHandleW(null), memory, IntPtr.Zero, &DialogProc, 0);
            return ended == 1 ? answer : null;
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
                    Place(window);
                    if (!SetForegroundWindow(window))
                    {
                        var flash = new FlashInfo { Size = (uint)sizeof(FlashInfo), Window = window, Flags = 3 | 12, Count = 3 }; // FLASHW_ALL | FLASHW_TIMERNOFG
                        FlashWindowEx(ref flash);
                    }
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

    /// <summary>For tests: the class names of the controls of the dialog made hidden from <see cref="Template"/>, destroyed at once.</summary>
    internal static string[] HiddenControls()
    {
        var template = Template("Search", "OK", "Cancel");
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
                foreach (var id in new[] { IdPrompt, IdEdit, IdOk, IdCancel })
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

    /// <summary>The dialog template: prompt, edit field, Search and Cancel, in dialog units with Segoe UI 9.</summary>
    internal static byte[] Template(string title, string ok, string cancel)
    {
        const uint popup = 0x80000000, visible = 0x10000000, child = 0x40000000, caption = 0x00C00000, sysMenu = 0x00080000, tabStop = 0x00010000;
        const uint border = 0x00800000, setFont = 0x40, modalFrame = 0x80, autoHScroll = 0x80, defPush = 0x01;
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
        void Item(uint style, short x, short y, short cx, short cy, ushort id, ushort atom, string text)
        {
            Align();
            writer.Write(style | child | visible);
            writer.Write(0u);
            writer.Write(x);
            writer.Write(y);
            writer.Write(cx);
            writer.Write(cy);
            writer.Write(id);
            writer.Write((ushort)0xFFFF);
            writer.Write(atom);
            Text(text);
            writer.Write((ushort)0); // no creation data
        }

        writer.Write(popup | caption | sysMenu | setFont | modalFrame);
        writer.Write(0x00040000u); // WS_EX_APPWINDOW: a taskbar button, so it can't get lost behind Explorer
        writer.Write((ushort)4);
        writer.Write((short)0);
        writer.Write((short)0);
        writer.Write((short)260);
        writer.Write((short)62);
        writer.Write((ushort)0); // no menu
        writer.Write((ushort)0); // standard dialog class
        Text(title);
        writer.Write((ushort)9);
        Text("Segoe UI");
        Item(0, 7, 7, 246, 10, IdPrompt, 0x0082, ""); // STATIC
        Item(border | tabStop | autoHScroll, 7, 20, 246, 14, IdEdit, 0x0081, ""); // EDIT
        Item(defPush | tabStop, 149, 41, 50, 14, IdOk, 0x0080, ok); // BUTTON
        Item(tabStop, 203, 41, 50, 14, IdCancel, 0x0080, cancel);
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
