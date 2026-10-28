using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Vfs365.Agent;
using Vfs365.Core.Drive;

namespace Vfs365.Tests;

/// <summary>Skips when vfs365shell.dll isn't built for this process's architecture (./build.ps1 shell).</summary>
public sealed class ShellFactAttribute : FactAttribute
{
    public ShellFactAttribute()
    {
        if (ShellDll.Path is null)
        {
            Skip = "vfs365shell.dll isn't built for this architecture: run ./build.ps1 shell";
        }
    }
}

/// <summary>vfs365shell.dll's test exports, loaded once (it keeps state: each test points it at its own section first).</summary>
static class ShellDll
{
    public static readonly string? Path = Find();

    static readonly Lazy<IntPtr> Library = new(() => NativeLibrary.Load(Path!));

    static string? Find()
    {
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => null,
        };
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); arch is not null && folder is not null; folder = folder.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(folder.FullName, "Vfs365.slnx")))
            {
                var dll = System.IO.Path.Combine(folder.FullName, "artifacts", "shell", arch, "vfs365shell.dll");
                return File.Exists(dll) ? dll : null;
            }
        }
        return null;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate int QueryStateFn(string path, uint attributes);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate int IsOnVolumeFn(string path);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate int VerbFn(int index, string path, int folder, StringBuilder name, StringBuilder title, int capacity);

    [UnmanagedFunctionPointer(CallingConvention.Winapi, CharSet = CharSet.Unicode)]
    delegate void TestSectionFn(string? name);

    static T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(Library.Value, name));

    public static int QueryState(string path, bool directory = false) =>
        Export<QueryStateFn>("VfsShellQueryState")(path, directory ? 0x10u : 0x80u);

    public static bool IsOnVolume(string path) => Export<IsOnVolumeFn>("VfsShellIsOnVolume")(path) == 1;

    /// <summary>A command (0 the VFS365 item, 1 to 7 the verbs) for an item; null past the last.</summary>
    public static (string Name, string Title, bool Shown)? Verb(int index, string path, bool folder)
    {
        var name = new StringBuilder(64);
        var title = new StringBuilder(256);
        var shown = Export<VerbFn>("VfsShellVerb")(index, path, folder ? 1 : 0, name, title, 256);
        return shown < 0 ? null : (name.ToString(), title.ToString(), shown == 1);
    }

    public static void UseSection(string? name) => Export<TestSectionFn>("VfsShellTestSection")(name);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int GetClassObjectFn(in Guid clsid, in Guid iid, out IntPtr factory);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    delegate int CanUnloadNowFn();

    static readonly Guid ClassFactoryIid = new("00000001-0000-0000-C000-000000000046");

    /// <summary>An object of one of the DLL's classes, through DllGetClassObject and IClassFactory as COM gets it.</summary>
    public static T Create<T>(Guid clsid) where T : class
    {
        Marshal.ThrowExceptionForHR(Export<GetClassObjectFn>("DllGetClassObject")(clsid, ClassFactoryIid, out var pointer));
        var factory = (IClassFactory)Marshal.GetObjectForIUnknown(pointer);
        Marshal.Release(pointer);
        var iid = typeof(T).GUID;
        Marshal.ThrowExceptionForHR(factory.CreateInstance(IntPtr.Zero, iid, out var instance));
        var created = (T)Marshal.GetObjectForIUnknown(instance);
        Marshal.Release(instance);
        return created;
    }

    public static int GetClassObject(Guid clsid, Guid iid) => Export<GetClassObjectFn>("DllGetClassObject")(clsid, iid, out _);

    public static int CanUnloadNow() => Export<CanUnloadNowFn>("DllCanUnloadNow")();
}

[ComImport, Guid("00000001-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IClassFactory
{
    [PreserveSig] int CreateInstance(IntPtr outer, in Guid iid, out IntPtr instance);
    [PreserveSig] int LockServer(bool locked);
}

[ComImport, Guid("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IExplorerCommand
{
    [PreserveSig] int GetTitle(IntPtr items, out IntPtr name);
    [PreserveSig] int GetIcon(IntPtr items, out IntPtr icon);
    [PreserveSig] int GetToolTip(IntPtr items, out IntPtr tip);
    [PreserveSig] int GetCanonicalName(out Guid name);
    [PreserveSig] int GetState(IntPtr items, bool slow, out uint state);
    [PreserveSig] int Invoke(IntPtr items, IntPtr context);
    [PreserveSig] int GetFlags(out uint flags);
    [PreserveSig] int EnumSubCommands(out IEnumExplorerCommand commands);
}

[ComImport, Guid("A88826F8-186F-4987-AADE-EA0CEF8FBFE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IEnumExplorerCommand
{
    [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IExplorerCommand[] commands, out uint fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumExplorerCommand copy);
}

[ComImport, Guid("0C6C4200-C589-11D0-999A-00C04FD655E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IShellIconOverlayIdentifier
{
    [PreserveSig] int IsMemberOf([MarshalAs(UnmanagedType.LPWStr)] string path, uint attributes);
    [PreserveSig] int GetOverlayInfo([MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int capacity, out int index, out uint flags);
    [PreserveSig] int GetPriority(out int priority);
}

/// <summary>
/// A shared section as the agent writes it (the layout ShellSection writes), under a name of its own: header, roots, strings, then
/// the slots. Owned by the user, as the DLL requires.
/// </summary>
sealed class TestSection : IDisposable
{
    public const int SectionSize = 2097152;
    readonly IntPtr mapping, view;

    public string Name { get; } = $@"Local\VFS365.Shell.Test.{Guid.NewGuid():N}";

    public TestSection(int size = SectionSize)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW($"O:{sid}D:P(A;;GA;;;{sid})", 1, out var descriptor, IntPtr.Zero))
        {
            throw new Win32Exception();
        }
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            mapping = CreateFileMappingW(new IntPtr(-1), ref attributes, 0x04, 0, (uint)size, Name); // PAGE_READWRITE
        }
        finally
        {
            LocalFree(descriptor);
        }
        if (mapping == IntPtr.Zero)
        {
            throw new Win32Exception();
        }
        view = MapViewOfFile(mapping, 0x2, 0, 0, (nuint)size); // FILE_MAP_WRITE
        if (view == IntPtr.Zero)
        {
            throw new Win32Exception();
        }
    }

    /// <summary>Writes the whole section. <paramref name="states"/>: volume path ("\Docs\a.docx") to slot state.</summary>
    public void Write((string Prefix, string Target)[] roots, IReadOnlyDictionary<string, string> strings, IReadOnlyDictionary<string, int> states,
        int processId, uint flags = 3, int sequence = 2, int slotCount = 64)
    {
        var bytes = new byte[SectionSize];
        var at = 64;
        var rootsOffset = at;
        foreach (var (prefix, target) in roots)
        {
            at = Record(bytes, at, prefix, target);
        }
        var stringsOffset = at;
        foreach (var (key, value) in strings)
        {
            at = Record(bytes, at, key, value);
        }
        var slotsOffset = (at + 15) & ~15;
        foreach (var (path, state) in states)
        {
            var hash = Hash(path.ToUpperInvariant());
            var slot = (int)(hash & (ulong)(slotCount - 1));
            while (BitConverter.ToUInt64(bytes, slotsOffset + slot * 16) != 0)
            {
                slot = (slot + 1) & (slotCount - 1);
            }
            BitConverter.TryWriteBytes(bytes.AsSpan(slotsOffset + slot * 16), hash);
            BitConverter.TryWriteBytes(bytes.AsSpan(slotsOffset + slot * 16 + 8), state);
        }
        uint[] header = [0x35363356, 1, (uint)sequence, (uint)processId, flags, (uint)rootsOffset, (uint)roots.Length, (uint)stringsOffset,
            (uint)strings.Count, (uint)slotsOffset, (uint)slotCount, (uint)states.Count];
        for (var i = 0; i < header.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), header[i]);
        }
        Marshal.Copy(bytes, 0, view, bytes.Length);
    }

    public void WriteInt32(int offset, int value) => Marshal.WriteInt32(view, offset, value);

    static int Record(byte[] bytes, int at, string first, string second)
    {
        BitConverter.TryWriteBytes(bytes.AsSpan(at), (ushort)first.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(at + 2), (ushort)second.Length);
        Encoding.Unicode.GetBytes(first + second, bytes.AsSpan(at + 4));
        return (at + 4 + 2 * (first.Length + second.Length) + 3) & ~3;
    }

    /// <summary>FNV-1a 64 over the key's UTF-16 code units, low byte first; 0 becomes 1.</summary>
    public static ulong Hash(string key)
    {
        var hash = 0xcbf29ce484222325ul;
        foreach (var b in Encoding.Unicode.GetBytes(key))
        {
            hash = (hash ^ b) * 0x100000001b3ul;
        }
        return hash == 0 ? 1 : hash;
    }

    public void Dispose()
    {
        UnmapViewOfFile(view);
        CloseHandle(mapping);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileMappingW(IntPtr file, ref SecurityAttributes attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll")]
    static extern bool UnmapViewOfFile(IntPtr view);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>vfs365shell.dll's lookups against a section the test writes: menu, overlays and the verbs' filters.</summary>
public sealed class ShellExtensionTests
{
    const int None = 0, Cached = 1, Uploading = 2, Downloading = 3, Problem = 4, OnlineOnly = 5;

    static readonly (string, string)[] Roots = [(@"M:\", @"\"), (@"\\VFS365\ann\", @"\"), (@"Z:\", @"\Sites\Finance\Documents\")];

    static readonly Dictionary<string, int> States = new()
    {
        [@"\Docs\a.docx"] = Cached,
        [@"\Docs"] = Problem,
        [@"\Sites\Finance\Documents\Plan.xlsx"] = Uploading,
        [@"\Sites\Finance\Documents"] = Downloading,
        [@"\Café\Menu.pdf"] = Downloading,
        [@"\"] = Cached,
    };

    static readonly Dictionary<string, string> NoStrings = [];

    static TestSection Written(int? processId = null, uint flags = 3, Dictionary<string, string>? strings = null)
    {
        var section = new TestSection();
        section.Write(Roots, strings ?? [], States, processId ?? Environment.ProcessId, flags);
        ShellDll.UseSection(section.Name);
        return section;
    }

    [ShellFact]
    public void Paths_on_the_volume_get_their_state_by_any_of_its_names()
    {
        using var section = Written();
        Assert.Equal(Cached, ShellDll.QueryState(@"M:\Docs\a.docx"));
        Assert.Equal(Cached, ShellDll.QueryState(@"m:\docs\A.DOCX"));
        Assert.Equal(Cached, ShellDll.QueryState(@"\\?\M:\Docs\a.docx"));
        Assert.Equal(Cached, ShellDll.QueryState(@"\\VFS365\ann\Docs\a.docx"));
        Assert.Equal(Cached, ShellDll.QueryState(@"\\vfs365\ANN\Docs\a.docx"));
        Assert.Equal(Cached, ShellDll.QueryState(@"\\?\UNC\VFS365\ann\Docs\a.docx"));
        Assert.Equal(Uploading, ShellDll.QueryState(@"Z:\Plan.xlsx"));
        // Downloading has no overlay class: a file shows online only, a folder nothing
        Assert.Equal(None, ShellDll.QueryState(@"Z:", directory: true));
        Assert.Equal(None, ShellDll.QueryState(@"Z:\", directory: true));
        Assert.Equal(OnlineOnly, ShellDll.QueryState(@"M:\café\menu.pdf"));
        Assert.Equal(Problem, ShellDll.QueryState(@"M:\Docs", directory: true));
        Assert.Equal(Problem, ShellDll.QueryState(@"M:\Docs\", directory: true));
        Assert.Equal(Cached, ShellDll.QueryState(@"\\VFS365\ann", directory: true));
    }

    [ShellFact]
    public void Unlisted_files_are_online_only_and_unlisted_folders_get_nothing()
    {
        using var section = Written();
        Assert.Equal(OnlineOnly, ShellDll.QueryState(@"M:\Docs\b.docx"));
        Assert.Equal(OnlineOnly, ShellDll.QueryState(@"Z:\Sub\c.txt"));
        Assert.Equal(None, ShellDll.QueryState(@"M:\Other", directory: true));
    }

    [ShellFact]
    public void Paths_elsewhere_or_with_dot_segments_get_nothing()
    {
        using var section = Written();
        Assert.Equal(None, ShellDll.QueryState(@"C:\Docs\a.docx"));
        Assert.Equal(None, ShellDll.QueryState(@"\\VFS365\bob\Docs\a.docx"));
        Assert.Equal(None, ShellDll.QueryState(@"\\VFS365\annie\Docs\a.docx"));
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\..\Docs\a.docx"));
        Assert.Equal(None, ShellDll.QueryState(@"M:\.\Docs\a.docx"));
        Assert.Equal(None, ShellDll.QueryState(""));
        Assert.Equal(None, ShellDll.QueryState(@"M:\" + new string('x', 5000)));
        Assert.False(ShellDll.IsOnVolume(@"C:\Docs\a.docx"));
        Assert.False(ShellDll.IsOnVolume(@"\\VFS365\bob\x.txt"));
    }

    [ShellFact]
    public void The_menu_shows_on_the_volume_only()
    {
        using var section = Written();
        Assert.True(ShellDll.IsOnVolume(@"M:\Docs\b.docx"));
        Assert.True(ShellDll.IsOnVolume(@"\\VFS365\ann\Docs"));
        Assert.True(ShellDll.IsOnVolume(@"Z:\Plan.xlsx"));
        Assert.False(ShellDll.IsOnVolume(@"Y:\Plan.xlsx"));
    }

    [ShellFact]
    public void Flags_switch_the_menu_and_the_overlays_off()
    {
        using (Written(flags: 1))
        {
            Assert.True(ShellDll.IsOnVolume(@"M:\Docs\a.docx"));
            Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));
        }
        using (Written(flags: 2))
        {
            Assert.False(ShellDll.IsOnVolume(@"M:\Docs\a.docx"));
            Assert.Equal(Cached, ShellDll.QueryState(@"M:\Docs\a.docx"));
        }
    }

    [ShellFact]
    public void Nothing_shows_once_the_agent_has_stopped()
    {
        using var ended = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/d /c exit") { CreateNoWindow = true, UseShellExecute = false })!;
        ended.WaitForExit();
        using var section = Written(processId: ended.Id);
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));
        Assert.False(ShellDll.IsOnVolume(@"M:\Docs\a.docx"));
    }

    [ShellFact]
    public void A_missing_small_or_busy_section_shows_nothing()
    {
        ShellDll.UseSection($@"Local\VFS365.Shell.Test.{Guid.NewGuid():N}");
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));
        Assert.False(ShellDll.IsOnVolume(@"M:\Docs\a.docx"));

        using (var small = new TestSection(4096))
        {
            ShellDll.UseSection(small.Name);
            Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));
        }

        using var section = Written();
        section.WriteInt32(8, 3); // odd: the writer is busy
        ShellDll.UseSection(section.Name);
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));
        section.WriteInt32(8, 4);
        Assert.Equal(Cached, ShellDll.QueryState(@"M:\Docs\a.docx"));
    }

    [ShellFact]
    public void A_damaged_section_shows_nothing_and_never_reads_past_its_end()
    {
        using var section = Written();
        section.WriteInt32(20, TestSection.SectionSize - 2); // roots offset at the very end
        ShellDll.UseSection(section.Name);
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));

        section.Write(Roots, NoStrings, States, Environment.ProcessId);
        section.WriteInt32(40, 48); // slot count not a power of two
        ShellDll.UseSection(section.Name);
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));

        section.Write(Roots, NoStrings, States, Environment.ProcessId);
        section.WriteInt32(36, TestSection.SectionSize - 64); // slots past the end
        ShellDll.UseSection(section.Name);
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));

        section.Write(Roots, NoStrings, States, Environment.ProcessId);
        section.WriteInt32(0, 0); // no magic
        ShellDll.UseSection(section.Name);
        Assert.Equal(None, ShellDll.QueryState(@"M:\Docs\a.docx"));
        Assert.False(ShellDll.IsOnVolume(@"M:\Docs\a.docx"));
    }

    [ShellFact]
    public void A_change_in_the_section_is_seen_at_once()
    {
        using var section = Written();
        Assert.Equal(Cached, ShellDll.QueryState(@"M:\Docs\a.docx"));
        section.Write(Roots, NoStrings, new Dictionary<string, int> { [@"\Docs\a.docx"] = Uploading }, Environment.ProcessId, sequence: 4);
        Assert.Equal(Uploading, ShellDll.QueryState(@"M:\Docs\a.docx"));
    }

    [ShellFact]
    public void The_verbs_match_the_legacy_menu()
    {
        ShellDll.UseSection($@"Local\VFS365.Shell.Test.{Guid.NewGuid():N}");
        var names = Enumerable.Range(1, 10).Select(i => ShellDll.Verb(i, @"M:\a.docx", false)).TakeWhile(v => v is not null).Select(v => v!.Value.Name);
        Assert.Equal(ExplorerVerbs.Verbs.Select(v => v.Name), names);
        for (var i = 0; i < ExplorerVerbs.Verbs.Length; i++)
        {
            var (name, files, folders) = ExplorerVerbs.Verbs[i];
            Assert.Equal(folders, ShellDll.Verb(i + 1, @"M:\Docs", true)!.Value.Shown);
            Assert.Equal(files && name is not ("keep" or "compare"), ShellDll.Verb(i + 1, @"M:\Docs\a.docx", false)!.Value.Shown);
        }
        Assert.Equal("VFS365", ShellDll.Verb(0, @"M:\a.docx", false)!.Value.Title);
        Assert.Equal("Open in browser", ShellDll.Verb(1, @"M:\a.docx", false)!.Value.Title);
    }

    [ShellFact]
    public void Open_in_desktop_app_is_for_Office_files_only()
    {
        ShellDll.UseSection(null);
        var desktop = Array.FindIndex(ExplorerVerbs.Verbs, v => v.Name == "desktop") + 1;
        foreach (var extension in ExplorerVerbs.OfficeSchemes.Keys)
        {
            Assert.True(ShellDll.Verb(desktop, $@"M:\Docs\Offer{extension.ToUpperInvariant()}", false)!.Value.Shown, extension);
        }
        Assert.False(ShellDll.Verb(desktop, @"M:\Docs\notes.txt", false)!.Value.Shown);
        Assert.False(ShellDll.Verb(desktop, @"M:\Docs.docx\notes", false)!.Value.Shown);
        Assert.False(ShellDll.Verb(desktop, @"M:\Docs\Offer", false)!.Value.Shown);
    }

    [ShellFact]
    public void Keep_and_compare_are_for_the_copies_the_engine_makes()
    {
        ShellDll.UseSection(null);
        var keep = Array.FindIndex(ExplorerVerbs.Verbs, v => v.Name == "keep") + 1;
        var compare = Array.FindIndex(ExplorerVerbs.Verbs, v => v.Name == "compare") + 1;
        string[] copies = ["Offer (conflict 2026-10-25 143012).docx", "Offer (recovered 2026-10-25 143012 2).docx", "notes (CONFLICT 2026-01-02 030405)"];
        string[] others = ["Offer.docx", "Offer (conflict 2026-10-25).docx", "Offer (conflicts 2026-10-25 143012).docx", "(conflict 2026-10-25 143012).docx"];
        foreach (var name in copies)
        {
            Assert.NotNull(ConflictCopies.OriginalName(name.Replace("CONFLICT", "conflict")));
            Assert.True(ShellDll.Verb(keep, $@"M:\Docs\{name}", false)!.Value.Shown, name);
            Assert.True(ShellDll.Verb(compare, $@"M:\Docs\{name}", false)!.Value.Shown, name);
            Assert.False(ShellDll.Verb(keep, $@"M:\Docs\{name}", true)!.Value.Shown, name);
        }
        foreach (var name in others)
        {
            Assert.False(ShellDll.Verb(keep, $@"M:\Docs\{name}", false)!.Value.Shown, name);
        }
    }

    [ShellFact]
    public void Titles_come_from_the_section_in_the_user_language()
    {
        using var section = Written(strings: new() { ["Verb.Menu"] = "VFS365 test", ["Verb.link"] = "Koppeling kopiëren" });
        Assert.Equal("VFS365 test", ShellDll.Verb(0, @"M:\a.docx", false)!.Value.Title);
        Assert.Equal("Koppeling kopiëren", ShellDll.Verb(3, @"M:\a.docx", false)!.Value.Title);
        Assert.Equal("Version history", ShellDll.Verb(4, @"M:\a.docx", false)!.Value.Title);
    }

    // Class IDs as in vfs365shell.c, src\Vfs365.Shell\package\AppxManifest.xml and installer\Package.wxs; the overlays with their states
    static readonly Guid MenuClass = new("A7AED2F3-B922-46C7-BBAB-64B986061C74");
    static readonly (Guid Class, int State)[] OverlayClasses =
    [
        (new("9DB55147-CB02-47F4-A386-E0EE3D556021"), Cached), (new("B7C851F1-2B15-49B3-B30E-769011CF84D2"), Uploading),
        (new("446CAE69-A613-4F61-B33B-6845715EF0E8"), Problem), (new("04668249-3350-4510-9629-814408AA98D3"), OnlineOnly),
    ];
    // The class Downloading had until 0.7.3: Explorer loads only 11 overlay handlers and OneDrive takes 7
    static readonly Guid DownloadingClass = new("372835A3-0CA9-4E2C-936E-42827149C031");

    static string Take(IntPtr text)
    {
        var value = Marshal.PtrToStringUni(text)!;
        Marshal.FreeCoTaskMem(text);
        return value;
    }

    [ShellFact]
    public void The_menu_is_a_VFS365_item_with_the_verbs_below_it()
    {
        ShellDll.UseSection(null);
        var menu = ShellDll.Create<IExplorerCommand>(MenuClass);
        Assert.Equal(0, menu.GetTitle(IntPtr.Zero, out var title));
        Assert.Equal("VFS365", Take(title));
        Assert.Equal(0, menu.GetIcon(IntPtr.Zero, out var icon));
        Assert.EndsWith(@"\vfs365-agent.exe,0", Take(icon));
        Assert.Equal(0, menu.GetFlags(out var flags));
        Assert.Equal(1u, flags); // ECF_HASSUBCOMMANDS
        Assert.Equal(0, menu.GetState(IntPtr.Zero, false, out var state));
        Assert.Equal(2u, state); // ECS_HIDDEN without a selection

        Assert.Equal(0, menu.EnumSubCommands(out var verbs));
        var commands = new IExplorerCommand[10];
        Assert.Equal(1, verbs.Next(10, commands, out var fetched)); // S_FALSE: fewer than asked
        Assert.Equal((uint)ExplorerVerbs.Verbs.Length, fetched);
        var titles = commands.Take((int)fetched).Select(c => c.GetTitle(IntPtr.Zero, out var t) == 0 ? Take(t) : null);
        Assert.Equal(["Open in browser", "Open in desktop app", "Copy link", "Version history", "Share in browser", "Keep this version", "Compare with the original",
            "Search in Microsoft 365"], titles);
        Assert.Equal(0, commands[5].GetFlags(out var keepFlags));
        Assert.Equal(0x20u, keepFlags); // ECF_SEPARATORBEFORE
        Assert.Equal(0, verbs.Reset());
        Assert.Equal(0, verbs.Skip(6));
        Assert.Equal(0, verbs.Clone(out var copy));
        var last = new IExplorerCommand[1];
        Assert.Equal(0, copy.Next(1, last, out _));
        Assert.Equal(0, last[0].GetTitle(IntPtr.Zero, out var lastTitle));
        Assert.Equal("Compare with the original", Take(lastTitle));
        Assert.Equal(0, copy.Next(1, last, out _));
        Assert.Equal(0, last[0].GetTitle(IntPtr.Zero, out var searchTitle));
        Assert.Equal("Search in Microsoft 365", Take(searchTitle));
        Assert.Equal(0, commands[7].GetFlags(out var searchFlags));
        Assert.Equal(0x20u, searchFlags); // ECF_SEPARATORBEFORE
        Assert.Equal(1, copy.Next(1, last, out var none));
        Assert.Equal(0u, none);
    }

    [ShellFact]
    public void Each_overlay_class_claims_its_own_state_with_its_own_icon()
    {
        using var section = Written();
        var overlays = OverlayClasses.Select(o => ShellDll.Create<IShellIconOverlayIdentifier>(o.Class)).ToArray();
        // A path in each class's state, in OverlayClasses' order; a downloading file (Café) counts as online only
        string[] paths = [@"M:\Docs\a.docx", @"Z:\Plan.xlsx", @"M:\Docs", @"M:\Docs\b.docx"];
        for (var i = 0; i < overlays.Length; i++)
        {
            var file = new StringBuilder(1024);
            Assert.Equal(0, overlays[i].GetOverlayInfo(file, file.Capacity, out var index, out var infoFlags));
            Assert.Equal(ShellDll.Path, file.ToString(), ignoreCase: true);
            Assert.Equal(OverlayClasses[i].State - 1, index);
            Assert.Equal(3u, infoFlags); // ISIOI_ICONFILE | ISIOI_ICONINDEX
            for (var j = 0; j < paths.Length; j++)
            {
                var attributes = paths[j] == @"M:\Docs" ? 0x10u : 0x80u;
                Assert.Equal(i == j ? 0 : 1, overlays[i].IsMemberOf(paths[j], attributes));
            }
            Assert.Equal(OverlayClasses[i].State == OnlineOnly ? 0 : 1, overlays[i].IsMemberOf(@"M:\Café\Menu.pdf", 0x80u));
        }
        var priorities = overlays.Select(o => o.GetPriority(out var p) == 0 ? p : -1).ToArray();
        Assert.Equal(priorities.Min(), priorities[Array.FindIndex(OverlayClasses, o => o.State == Problem)]);
        Assert.Equal(unchecked((int)0x80040111), ShellDll.GetClassObject(DownloadingClass, new Guid("00000001-0000-0000-C000-000000000046"))); // CLASS_E_CLASSNOTAVAILABLE
    }

    [ShellFact]
    public void Unknown_classes_are_refused()
    {
        Assert.Equal(unchecked((int)0x80040111), ShellDll.GetClassObject(Guid.NewGuid(), new Guid("00000001-0000-0000-C000-000000000046"))); // CLASS_E_CLASSNOTAVAILABLE
        Assert.Equal(unchecked((int)0x80004002), ShellDll.GetClassObject(MenuClass, Guid.NewGuid())); // E_NOINTERFACE
    }

    [ShellFact]
    public void The_agents_writer_and_the_dll_agree()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var name = $@"Local\VFS365.Shell.Test.{Guid.NewGuid():N}";
        using var section = ShellSection.Open(name, identity.User!);
        var image = new byte[ShellImage.SectionSize];
        ShellImage.Build(image, (uint)Environment.ProcessId, ShellImage.MenuFlag | ShellImage.OverlaysFlag,
            [new ShellRoot(@"M:\", @"\"), new ShellRoot(@"Z:\", @"\Sites\Finance\Documents\"), new ShellRoot(@"\\VFS365\ann\", @"\")],
            [("Verb.Menu", "Mijn menu")],
            [(ShellImage.KeyHash(@"\OneDrive\a.docx"), FileState.Uploading), (ShellImage.KeyHash(@"\Sites\Finance\Documents\Café.xlsx"), FileState.Problem)]);
        section.Write(image);
        ShellDll.UseSection(name);
        try
        {
            Assert.Equal(Uploading, ShellDll.QueryState(@"\\VFS365\ann\onedrive\A.DOCX"));
            Assert.Equal(Problem, ShellDll.QueryState(@"Z:\CAFÉ.xlsx"));
            Assert.Equal(Problem, ShellDll.QueryState(@"M:\Sites\Finance\Documents\café.xlsx"));
            Assert.Equal(OnlineOnly, ShellDll.QueryState(@"M:\OneDrive\b.docx"));
            Assert.False(ShellDll.IsOnVolume(@"C:\Users"));
            Assert.Equal("Mijn menu", ShellDll.Verb(0, @"M:\OneDrive\a.docx", false)?.Title);
        }
        finally
        {
            ShellDll.UseSection(null);
        }
    }
}
