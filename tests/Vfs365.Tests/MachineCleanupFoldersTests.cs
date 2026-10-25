using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>
/// The uninstall puts known folders left in VFS365 back: as SYSTEM in signed-in users' hives, and with built-in tools at the next
/// sign-in for the others. Each test works in a registry key of its own (HKCU\Software\VFS365-tests\...) standing in for a hive.
/// </summary>
public sealed class MachineCleanupFoldersTests : IDisposable
{
    const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    static readonly Guid DocumentsId = new("fdd39ad0-238f-46af-adb4-6c85480369c7"), ThisPcDocumentsId = new("f42ee2d3-909f-4907-8871-4c22fc0bf756");

    readonly string keyPath = $@"Software\VFS365-tests\{Guid.NewGuid():N}";
    readonly string folder = Path.Combine(Path.GetTempPath(), "vfs365-cleanup", Guid.NewGuid().ToString("N"));
    readonly RegistryKey hive;

    public MachineCleanupFoldersTests() => hive = Registry.CurrentUser.CreateSubKey(keyPath);

    public void Dispose()
    {
        hive.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        using (var tests = Registry.CurrentUser.OpenSubKey(@"Software\VFS365-tests", writable: true))
        {
            if (tests is not null && tests.SubKeyCount == 0 && tests.ValueCount == 0)
            {
                Registry.CurrentUser.DeleteSubKey(@"Software\VFS365-tests", throwOnMissingSubKey: false);
            }
        }
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    RegistryKey ShellFolders(string name = "User Shell Folders") => hive.CreateSubKey($@"{Explorer}\{name}");

    static string? Raw(RegistryKey key, string name) => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    [Fact]
    public void Folders_left_in_VFS365_go_back_where_they_were_or_to_the_default()
    {
        using (var user = ShellFolders())
        {
            user.SetValue("Personal", @"M:\OneDrive\Documents", RegistryValueKind.ExpandString);
            user.SetValue("{F42EE2D3-909F-4907-8871-4C22FC0BF756}", @"M:\OneDrive\Documents", RegistryValueKind.ExpandString);
            user.SetValue("Desktop", @"\\vfs365\JAN\OneDrive\Desktop", RegistryValueKind.ExpandString);
            user.SetValue("My Music", @"\\VFS365server\share\Music", RegistryValueKind.ExpandString);
            user.SetValue("My Video", @"%USERPROFILE%\Videos", RegistryValueKind.ExpandString);
            user.SetValue("{11111111-2222-3333-4444-555555555555}", @"\\VFS365\jan\Other", RegistryValueKind.ExpandString);
        }
        using (var legacy = ShellFolders("Shell Folders"))
        {
            legacy.SetValue("Personal", @"M:\OneDrive\Documents");
        }
        var state = FolderRedirection.ParseState($$$"""
            {"{{{DocumentsId}}}":{"Folder":"Documents","Ids":["{{{DocumentsId}}}","{{{ThisPcDocumentsId}}}"],"Original":"C:\\Users\\jan\\My Docs","Target":"M:\\OneDrive\\Documents"}}
            """);
        var said = new List<string>();

        var reset = MachineCleanup.RestoreKnownFolders(hive, @"C:\Users\jan", state, new HashSet<string>(["M:"], StringComparer.OrdinalIgnoreCase), false, said.Add);

        Assert.Equal(5, reset);
        using var after = ShellFolders();
        Assert.Equal(@"%USERPROFILE%\My Docs", Raw(after, "Personal")); // where VFS365 found it, as Windows writes it
        Assert.Equal(@"%USERPROFILE%\My Docs", Raw(after, "{F42EE2D3-909F-4907-8871-4C22FC0BF756}"));
        Assert.Equal(RegistryValueKind.ExpandString, after.GetValueKind("Personal"));
        Assert.Equal(@"%USERPROFILE%\Desktop", Raw(after, "Desktop")); // no record: Windows' default
        Assert.Equal(@"\\VFS365server\share\Music", Raw(after, "My Music")); // another server
        Assert.Equal(@"%USERPROFILE%\Videos", Raw(after, "My Video"));
        Assert.Null(Raw(after, "{11111111-2222-3333-4444-555555555555}")); // unknown folder: removed, Windows uses its default
        using var legacyAfter = ShellFolders("Shell Folders");
        Assert.Equal(@"C:\Users\jan\My Docs", Raw(legacyAfter, "Personal"));
        Assert.Equal(5, said.Count);
    }

    [Fact]
    public void A_dry_run_changes_nothing()
    {
        using (var user = ShellFolders())
        {
            user.SetValue("Desktop", @"\\VFS365\jan\OneDrive\Desktop", RegistryValueKind.ExpandString);
        }

        Assert.Equal(1, MachineCleanup.RestoreKnownFolders(hive, @"C:\Users\jan", [], new HashSet<string>(), true, _ => { }));

        using var after = ShellFolders();
        Assert.Equal(@"\\VFS365\jan\OneDrive\Desktop", Raw(after, "Desktop"));
    }

    [Theory]
    [InlineData(@"\\VFS365\jan\OneDrive", true)]
    [InlineData(@"\\vfs365\ann", true)]
    [InlineData(@"\\VFS365server\x", false)]
    [InlineData(@"M:\OneDrive\Desktop", true)]
    [InlineData(@"m:", true)]
    [InlineData(@"M:x", false)]
    [InlineData(@"N:\Desktop", false)]
    [InlineData(@"%USERPROFILE%\Desktop", false)]
    public void Paths_in_VFS365_are_on_its_name_or_its_letter(string path, bool inside) =>
        Assert.Equal(inside, MachineCleanup.InVfs365(path, new HashSet<string>(["M:"], StringComparer.OrdinalIgnoreCase)));

    [Fact]
    public void A_registry_link_is_never_followed()
    {
        using (var elsewhere = hive.CreateSubKey("Elsewhere"))
        {
            elsewhere.SetValue("Desktop", @"\\VFS365\jan\OneDrive\Desktop", RegistryValueKind.ExpandString);
        }
        using (hive.CreateSubKey(Explorer))
        {
        }
        // User Shell Folders as a link to another key, as a user could plant one in their own hive
        if (!CreateLink($@"{keyPath}\{Explorer}\User Shell Folders", $@"\REGISTRY\USER\{WindowsIdentity.GetCurrent().User}\{keyPath}\Elsewhere"))
        {
            return; // this machine doesn't let the test make one
        }
        try
        {
            var said = new List<string>();

            Assert.Equal(0, MachineCleanup.RestoreKnownFolders(hive, @"C:\Users\jan", [], new HashSet<string>(), false, said.Add));

            Assert.Contains(said, line => line.Contains("registry link", StringComparison.Ordinal));
            using var elsewhere = hive.OpenSubKey("Elsewhere")!;
            Assert.Equal(@"\\VFS365\jan\OneDrive\Desktop", Raw(elsewhere, "Desktop"));
        }
        finally
        {
            DeleteLink($@"{keyPath}\{Explorer}\User Shell Folders");
        }
    }

    [Fact]
    public void The_state_is_read_from_the_profile_s_VFS365_folder()
    {
        var profile = new MachineCleanup.UserProfile(folder, WindowsIdentity.GetCurrent().User!);
        Directory.CreateDirectory(profile.DataFolder);
        File.WriteAllText(Path.Combine(profile.DataFolder, FolderRedirection.StateFileName), $$$"""
            {"{{{DocumentsId}}}":{"Folder":"Documents","Ids":["{{{DocumentsId}}}"],"Original":"C:\\Users\\jan\\Documents","Target":"\\\\VFS365\\jan\\OneDrive\\Documents"}}
            """);

        var record = Assert.Single(MachineCleanup.ReadState(profile));

        Assert.Equal(@"\\VFS365\jan\OneDrive\Documents", record.Target);
        Assert.Empty(MachineCleanup.ReadState(profile with { Folder = Path.Combine(folder, "missing") }));
    }

    [Fact]
    public void The_logon_cleanup_resets_folders_and_removes_every_drive_name_with_built_in_tools()
    {
        using (var user = ShellFolders())
        {
            user.SetValue("Personal", @"\\VFS365\jan\OneDrive\Documents", RegistryValueKind.ExpandString);
            user.SetValue("{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}", @"M:\OneDrive\Saved Games", RegistryValueKind.ExpandString);
            user.SetValue("Desktop", @"D:\Desktop", RegistryValueKind.ExpandString);
            user.SetValue("My Music", @"\\VFS365server\Music", RegistryValueKind.ExpandString);
        }
        foreach (var name in new[] { "##VFS365#jan", "##VFS365#jan#Sites#Finance#Documents", "##fileserver#share" })
        {
            using (hive.CreateSubKey($@"{Explorer}\MountPoints2\{name}"))
            {
            }
        }
        using (hive.CreateSubKey($@"Software\Classes\CLSID\{ExplorerLocation.Clsid}"))
        {
        }

        var command = MachineCleanup.LogonCleanupCommand(["M:"], $@"HKCU\{keyPath}");
        var arguments = command[(command.IndexOf("cmd.exe\" ", StringComparison.Ordinal) + "cmd.exe\" ".Length)..];
        using (var cmd = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!)
        {
            cmd.StandardOutput.ReadToEnd();
            Assert.True(cmd.WaitForExit(60_000));
        }

        using var after = ShellFolders();
        Assert.Equal(@"%USERPROFILE%\Documents", Raw(after, "Personal"));
        Assert.Equal(@"%USERPROFILE%\Saved Games", Raw(after, "{4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4}"));
        Assert.Equal(RegistryValueKind.ExpandString, after.GetValueKind("Personal"));
        Assert.Equal(@"D:\Desktop", Raw(after, "Desktop"));
        Assert.Equal(@"\\VFS365server\Music", Raw(after, "My Music"));
        using var mountPoints = hive.OpenSubKey($@"{Explorer}\MountPoints2")!;
        Assert.Equal(["##fileserver#share"], mountPoints.GetSubKeyNames());
        using var clsid = hive.OpenSubKey($@"Software\Classes\CLSID\{ExplorerLocation.Clsid}");
        Assert.Null(clsid);
    }

    // A registry symbolic link at HKCU\<path> to the native path <target>; false when Windows refuses it
    static bool CreateLink(string path, string target)
    {
        if (RegCreateKeyExW(Registry.CurrentUser.Handle, path, 0, null, 0x2, 0xF003F | 0x20, IntPtr.Zero, out var link, out _) != 0) // REG_OPTION_CREATE_LINK
        {
            return false;
        }
        using (link)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(target);
            if (RegSetValueExW(link, "SymbolicLinkValue", 0, 6, bytes, bytes.Length) == 0) // REG_LINK
            {
                return true;
            }
        }
        DeleteLink(path);
        return false;
    }

    static void DeleteLink(string path)
    {
        if (RegOpenKeyExW(Registry.CurrentUser.Handle, path, 0x8, 0x10000, out var link) == 0) // REG_OPTION_OPEN_LINK, DELETE
        {
            using (link)
            {
                NtDeleteKey(link);
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegCreateKeyExW(SafeRegistryHandle key, string subKey, int reserved, string? className, uint options, int access, IntPtr security,
        out SafeRegistryHandle result, out int disposition);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegSetValueExW(SafeRegistryHandle key, string name, int reserved, int type, byte[] data, int size);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, uint options, int access, out SafeRegistryHandle result);

    [DllImport("ntdll.dll")]
    static extern int NtDeleteKey(SafeRegistryHandle key);
}
