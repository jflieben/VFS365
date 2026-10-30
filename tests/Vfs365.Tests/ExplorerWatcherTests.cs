using System.Runtime.InteropServices;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>
/// The Explorer search watcher without an Explorer: which searches count as searches on the drive (tab by tab), what the watcher reads
/// from Explorer's windows, and that the COM wrappers it gets are released.
/// </summary>
public sealed class ExplorerWatcherTests
{
    const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", Home = "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}";
    static readonly string[] Roots = [@"M:\", @"\\VFS365\JosLieben\"];

    // Views as Shell Windows showed them in spike 15 (Windows 11 25H2)
    static ExplorerWindows.View Folder(string path, string name) => new("file:///" + path.Replace('\\', '/'), name, path);

    static ExplorerWindows.View Special(string parsing, string name) => new("", name, parsing);

    static ExplorerWindows.View Search(string scope, string words) => new("", words, $"Search Results in {scope}&{words}");

    /// <summary>The drive: M:\ folders are volume paths, anything else is elsewhere.</summary>
    static SearchTracker Tracker() => new(location => location.StartsWith(@"M:\", StringComparison.OrdinalIgnoreCase) ? location[2..] : null);

    /// <summary>Looks at the search until it acts (or doesn't within the longest wait): the verdict, null when it never acts.</summary>
    static SearchHit? Settle(SearchTracker tracker, long tab, ExplorerWindows.View view, ref long now, bool initial = false)
    {
        for (var waited = 0; waited <= SearchSettle.MaxWait + 500; waited += 250)
        {
            if (tracker.Look(tab, view, Roots, now += 250, initial) is { } hit)
            {
                return hit;
            }
        }
        return null;
    }

    [Fact]
    public void A_search_in_the_drive_acts_once_its_words_settle()
    {
        var tracker = Tracker();
        long now = 20_000;
        Assert.Null(tracker.Look(1, Folder(@"M:\Sites\X", "X"), Roots, now, initial: false));

        var hit = Settle(tracker, 1, Search(@"M:\Sites\X", "invoice"), ref now);

        Assert.NotNull(hit);
        Assert.Equal(SearchSettle.Verdict.Act, hit.Verdict);
        Assert.Equal(@"\Sites\X", hit.Volume);
        Assert.Equal(@"M:\Sites\X", hit.Location);
        Assert.Equal("invoice", hit.Terms);
        Assert.False(hit.Restored);
        Assert.False(tracker.Pending);
    }

    [Theory]
    [InlineData(ThisPc, "This PC")]
    [InlineData(Home, "Home")]
    [InlineData("::{679F85CB-0220-4080-B29B-5540CC05AAB6}", "Quick access")]
    [InlineData("::{031E4825-7B94-4DC3-B131-E946B44C8DD5}", "Libraries")]
    [InlineData("shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", "Home")]
    [InlineData(@"C:\Users\JosLieben\Documents", "Documents")]
    public void A_search_in_a_place_off_the_drive_is_left_alone_after_a_folder_of_the_drive(string parsing, string name)
    {
        var tracker = Tracker();
        long now = 20_000;
        Assert.Null(tracker.Look(1, Folder(@"M:\Sites\X", "X"), Roots, now, initial: false));
        Assert.Null(tracker.Look(1, parsing.StartsWith("::") || parsing.StartsWith("shell:") ? Special(parsing, name) : Folder(parsing, name), Roots, now += 300, initial: false));

        // The scope as Explorer names it: a path for a folder, the display name for This PC and the like
        var scope = parsing.Contains('\\') && !parsing.StartsWith("::") ? parsing : name;
        var hit = Settle(tracker, 1, Search(scope, "invoice"), ref now);

        Assert.Null(hit);
        Assert.False(tracker.Pending);
    }

    [Fact]
    public void A_search_in_this_pc_typed_after_a_folder_of_the_drive_does_not_bounce_the_tab()
    {
        var tracker = Tracker();
        long now = 20_000;
        tracker.Look(7, Folder(@"M:\Sites\X", "X"), Roots, now, false);
        tracker.Look(7, Special(ThisPc, "This PC"), Roots, now += 400, false);

        // Typing as Explorer does: every keystroke a new view
        foreach (var words in new[] { "i", "in", "inv", "invo", "invoice" })
        {
            Assert.Null(tracker.Look(7, Search("This PC", words), Roots, now += 300, false));
        }
        Assert.Null(Settle(tracker, 7, Search("This PC", "invoice"), ref now));
        Assert.False(tracker.Pending);
        Assert.Empty(tracker.PendingTabs);
    }

    [Fact]
    public void A_search_in_the_top_of_the_drive_named_by_its_label_counts()
    {
        var tracker = Tracker();
        long now = 20_000;
        tracker.Look(1, Folder(@"M:\", "VFS365"), Roots, now, false);

        var hit = Settle(tracker, 1, new ExplorerWindows.View("", "plan", "Search Results in VFS365 (M:)&plan"), ref now);

        Assert.NotNull(hit);
        Assert.Equal(@"M:\", hit.Location);
        Assert.Equal(@"\", hit.Volume);
    }

    [Fact]
    public void The_tab_before_is_remembered_per_tab()
    {
        var tracker = Tracker();
        long now = 20_000;
        tracker.Look(1, Folder(@"M:\", "VFS365"), Roots, now, false);
        tracker.Look(2, Special(ThisPc, "This PC"), Roots, now, false);

        // Tab 2 never showed the drive; a rootless name in it is nothing, in tab 1 it is the top of the drive
        Assert.Null(Settle(tracker, 2, new ExplorerWindows.View("", "plan", "Search Results in VFS365 (M:)&plan"), ref now));
        Assert.NotNull(Settle(tracker, 1, new ExplorerWindows.View("", "plan", "Search Results in VFS365 (M:)&plan"), ref now));
    }

    [Fact]
    public void A_search_that_was_showing_at_the_start_is_marked_restored()
    {
        var tracker = Tracker();
        long now = 20_000;

        var hit = Settle(tracker, 1, Search(@"M:\Sites\X", "invoice"), ref now, initial: true);

        Assert.NotNull(hit);
        Assert.True(hit.Restored);
    }

    [Fact]
    public void Leaving_the_search_and_searching_again_is_a_new_search_and_not_restored()
    {
        var tracker = Tracker();
        long now = 20_000;
        Assert.NotNull(Settle(tracker, 1, Search(@"M:\Sites\X", "invoice"), ref now, initial: true));

        tracker.Look(1, Folder(@"M:\Sites\X", "X"), Roots, now += 300, false);
        var again = Settle(tracker, 1, Search(@"M:\Sites\X", "invoice"), ref now);

        Assert.NotNull(again);
        Assert.False(again.Restored);
    }

    [Fact]
    public void A_search_still_showing_after_the_hand_off_is_stopped_again()
    {
        var tracker = Tracker();
        long now = 20_000;
        var view = Search(@"M:\Sites\X", "invoice");
        Assert.Equal(SearchSettle.Verdict.Act, Settle(tracker, 1, view, ref now)!.Verdict);

        var again = Settle(tracker, 1, view, ref now);

        Assert.Equal(SearchSettle.Verdict.StopAgain, again!.Verdict);
    }

    [Fact]
    public void A_closed_tab_is_forgotten()
    {
        var tracker = Tracker();
        long now = 20_000;
        tracker.Look(1, Folder(@"M:\Sites\X", "X"), Roots, now, false);
        Assert.Null(tracker.Look(1, Search(@"M:\Sites\X", "inv"), Roots, now += 300, false)); // settling
        Assert.True(tracker.Pending);
        Assert.Equal([1L], tracker.PendingTabs);

        tracker.Gone(1, now += 300);

        Assert.False(tracker.Pending);
        Assert.Empty(tracker.PendingTabs);
        // A new tab with the same identity does not inherit the folder shown before: a rootless name finds no scope
        Assert.Null(Settle(tracker, 1, new ExplorerWindows.View("", "plan", "Search Results in VFS365 (M:)&plan"), ref now));
    }

    [Fact]
    public void A_cleared_search_box_ends_the_wait()
    {
        var tracker = Tracker();
        long now = 20_000;
        tracker.Look(1, Folder(@"M:\Sites\X", "X"), Roots, now, false);
        Assert.Null(tracker.Look(1, Search(@"M:\Sites\X", "inv"), Roots, now += 300, false));
        Assert.True(tracker.Pending);

        var cleared = new ExplorerWindows.View("", @"Search Results in M:\Sites\X", @"Search Results in M:\Sites\X");
        Assert.Null(tracker.Look(1, cleared, Roots, now += 300, false));

        Assert.False(tracker.Pending);
    }

    [Fact]
    public void Only_file_explorer_windows_are_watched()
    {
        Assert.True(ExplorerWindows.IsFileExplorer("CabinetWClass"));
        Assert.True(ExplorerWindows.IsFileExplorer("ExploreWClass"));
        Assert.False(ExplorerWindows.IsFileExplorer("IEFrame"));
        Assert.False(ExplorerWindows.IsFileExplorer(""));
    }

    /// <summary>A Shell Windows stand-in: the members the late-bound calls use (public: the dynamic binder only sees public types).</summary>
    public sealed class FakeShellWindows(params object?[] items)
    {
        public int Count => items.Length;

        public object? Item(object index) => items[(int)index] is Exception e ? throw e : items[(int)index];
    }

    public sealed class FakeBrowser(string? url, string? name)
    {
        public string? LocationURL => url;

        public string? LocationName => name;
    }

    [Fact]
    public void Items_are_the_windows_in_order_up_to_the_maximum()
    {
        var one = new object();
        var two = new object();
        var three = new object();

        Assert.Equal([one, two, three], ExplorerWindows.Items(new FakeShellWindows(one, two, three), 64));
        Assert.Equal([one, two], ExplorerWindows.Items(new FakeShellWindows(one, two, three), 2));
        Assert.Empty(ExplorerWindows.Items(new FakeShellWindows(), 64));
    }

    [Fact]
    public void A_window_that_fails_on_its_own_is_left_out_and_reported()
    {
        var one = new object();
        var three = new object();
        var problems = new List<string>();

        var items = ExplorerWindows.Items(new FakeShellWindows(one, new COMException("gone", unchecked((int)0x800706BA)), three), 64, problems.Add);

        Assert.Equal([one, three], items);
        Assert.Single(problems);
        Assert.Contains("gone", problems[0]);
    }

    [Fact]
    public void A_tab_is_read_for_its_location_alone_when_it_has_no_shell_view()
    {
        var view = ExplorerWindows.ReadView(new FakeBrowser("file:///M:/Sites", "Sites"));

        Assert.Equal("file:///M:/Sites", view.LocationUrl);
        Assert.Equal("Sites", view.LocationName);
        Assert.Null(view.ParsingName); // not a shell browser: no view to ask
    }

    [Fact]
    public void Releasing_drops_one_reference_and_releasing_all_ends_the_wrapper()
    {
        var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
        if (type is null)
        {
            return; // no scripting runtime to borrow a COM object from
        }
        var wrapper = Activator.CreateInstance(type)!;
        var unknown = Marshal.GetIUnknownForObject(wrapper);
        var again = Marshal.GetObjectForIUnknown(unknown); // the same wrapper, one more reference (as every call that returns it adds)
        Marshal.Release(unknown);
        Assert.Same(wrapper, again);

        ExplorerWindows.Release(again);
        Marshal.Release(Marshal.GetIUnknownForObject(wrapper)); // still alive: the first reference holds it

        ExplorerWindows.Release(wrapper);
        Assert.Throws<InvalidComObjectException>(() => Marshal.GetIUnknownForObject(wrapper));
        ExplorerWindows.Release(wrapper); // already ended: no harm
        ExplorerWindows.ReleaseAll(wrapper);
    }

    [Fact]
    public void Releasing_all_ends_a_wrapper_whatever_its_references()
    {
        var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
        if (type is null)
        {
            return;
        }
        var wrapper = Activator.CreateInstance(type)!;
        var unknown = Marshal.GetIUnknownForObject(wrapper);
        _ = Marshal.GetObjectForIUnknown(unknown);
        _ = Marshal.GetObjectForIUnknown(unknown);
        Marshal.Release(unknown);

        ExplorerWindows.ReleaseAll(wrapper);

        Assert.Throws<InvalidComObjectException>(() => Marshal.GetIUnknownForObject(wrapper));
    }

    [Fact]
    public void Releasing_nothing_or_a_managed_object_does_nothing()
    {
        ExplorerWindows.Release(null);
        ExplorerWindows.ReleaseAll(null);
        ExplorerWindows.Release("not a wrapper");
        ExplorerWindows.ReleaseAll(new object());
    }

    [Fact]
    public void A_cancelled_call_is_told_from_other_failures()
    {
        Assert.True(ExplorerWindows.Cancelled(new COMException("cancelled", unchecked((int)0x80010002))));
        Assert.False(ExplorerWindows.Cancelled(new COMException("gone", unchecked((int)0x800706BA))));
        Assert.False(ExplorerWindows.Cancelled(new InvalidCastException()));
    }

    // The sink through IDispatch::Invoke, as Explorer calls it: DISPPARAMS with the arguments last first
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int InvokeCall(IntPtr self, int dispId, ref Guid riid, uint lcid, ushort flags, ref DispParams args, IntPtr result, IntPtr exception, IntPtr argError);

    [StructLayout(LayoutKind.Sequential)]
    struct DispParams
    {
        public IntPtr Args, NamedArgs;
        public int Count, NamedCount;
    }

    [DllImport("oleaut32.dll")]
    static extern int VariantClear(IntPtr variant);

    /// <summary>
    /// Fires a navigation event at the sink: the browser it concerns (a COM object, as a VT_DISPATCH that holds its own reference),
    /// <paramref name="byRefVariants"/> arguments passed by reference (VT_EMPTY) and, for BeforeNavigate2, the Cancel flag.
    /// </summary>
    static void Fire(object sink, int dispId, object browser, int byRefVariants, bool cancel)
    {
        const int Variant = 24, ByRef = 0x4000, VariantType = 12, BoolType = 11;
        var count = byRefVariants + 1 + (cancel ? 1 : 0);
        var args = Marshal.AllocCoTaskMem(count * Variant);
        var values = Marshal.AllocCoTaskMem(Math.Max(1, byRefVariants) * Variant);
        var flag = Marshal.AllocCoTaskMem(2);
        Marshal.Copy(new byte[count * Variant], 0, args, count * Variant);
        Marshal.Copy(new byte[Math.Max(1, byRefVariants) * Variant], 0, values, Math.Max(1, byRefVariants) * Variant);
        Marshal.WriteInt16(flag, 0);
        var browserVariant = args + (count - 1) * Variant;
        var pointer = Marshal.GetComInterfaceForObject(sink, typeof(DWebBrowserEvents2));
        try
        {
            var next = 0;
            if (cancel)
            {
                Marshal.WriteInt16(args, BoolType | ByRef);
                Marshal.WriteIntPtr(args, 8, flag);
                next++;
            }
            for (var i = 0; i < byRefVariants; i++, next++)
            {
                Marshal.WriteInt16(args + next * Variant, VariantType | ByRef);
                Marshal.WriteIntPtr(args + next * Variant, 8, values + i * Variant);
            }
            Marshal.GetNativeVariantForObject(browser, browserVariant);
            var invoke = Marshal.GetDelegateForFunctionPointer<InvokeCall>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), 6 * IntPtr.Size));
            var parameters = new DispParams { Args = args, Count = count };
            var riid = Guid.Empty;
            Assert.Equal(0, invoke(pointer, dispId, ref riid, 0, 1, ref parameters, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
        }
        finally
        {
            Marshal.Release(pointer);
            VariantClear(browserVariant);
            Marshal.FreeCoTaskMem(args);
            Marshal.FreeCoTaskMem(values);
            Marshal.FreeCoTaskMem(flag);
        }
    }

    [Theory]
    [InlineData(250, 5, true, false)] // BeforeNavigate2: no read needed, the other two follow
    [InlineData(252, 1, false, true)] // NavigateComplete2
    [InlineData(259, 1, false, true)] // DocumentComplete
    public void A_navigation_event_wakes_for_its_tab_and_releases_the_browser_it_brings(int dispId, int byRefVariants, bool cancel, bool wakes)
    {
        var type = Type.GetTypeFromProgID("Scripting.FileSystemObject");
        if (type is null)
        {
            return;
        }
        var woken = new List<long>();
        var sink = new ExplorerSearchWatcher.WakeSink(42, woken.Add);
        var browser = Activator.CreateInstance(type)!; // the wrapper the watcher holds for the tab: one reference

        Fire(sink, dispId, browser, byRefVariants, cancel);

        Assert.Equal(wakes ? new long[] { 42 } : [], woken);
        // The event added a reference to the same wrapper and the sink dropped it again: the watcher's own reference is the last
        Assert.Equal(0, Marshal.ReleaseComObject(browser));
    }

    [Fact]
    public void Shell_windows_events_wake_for_tab_zero()
    {
        var woken = new List<long>();
        var sink = new ExplorerSearchWatcher.WakeSink(0, woken.Add);
        var pointer = Marshal.GetComInterfaceForObject(sink, typeof(DShellWindowsEvents));
        try
        {
            var invoke = Marshal.GetDelegateForFunctionPointer<InvokeCall>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), 6 * IntPtr.Size));
            var args = Marshal.AllocCoTaskMem(24);
            try
            {
                Marshal.Copy(new byte[24], 0, args, 24);
                Marshal.WriteInt16(args, 3); // VT_I4: the cookie
                Marshal.WriteInt32(args, 8, 5);
                var parameters = new DispParams { Args = args, Count = 1 };
                var riid = Guid.Empty;
                Assert.Equal(0, invoke(pointer, 200, ref riid, 0, 1, ref parameters, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)); // WindowRegistered
                Assert.Equal(0, invoke(pointer, 201, ref riid, 0, 1, ref parameters, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)); // WindowRevoked
            }
            finally
            {
                Marshal.FreeCoTaskMem(args);
            }
        }
        finally
        {
            Marshal.Release(pointer);
        }
        Assert.Equal([0L, 0L], woken);
    }
}
