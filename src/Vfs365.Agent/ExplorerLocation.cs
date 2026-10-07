using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>
/// Explorer integration, all per user (HKCU, no admin rights): a navigation pane node (delegate folder) that shows the drive or UNC path,
/// registered in the 64-bit and 32-bit views so file dialogs of both kinds show it, and the name Explorer gives the network drive.
/// </summary>
static class ExplorerLocation
{
    public const string Clsid = "{3CB016A6-FE2C-45AC-ADBD-05A92CDE26CC}";
    const string Classes = @"Software\Classes";
    const string Explorer = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    const string NameSpace = Explorer + @"\Desktop\NameSpace\" + Clsid;
    const string HideFromDesktop = Explorer + @"\HideDesktopIcons\NewStartPanel";

    public static void Register(string name, string targetPath, string icon)
    {
        foreach (var view in new[] { @"CLSID\", @"WOW6432Node\CLSID\" })
        {
            using var clsid = Registry.CurrentUser.CreateSubKey($@"{Classes}\{view}{Clsid}");
            clsid.SetValue("", name);
            clsid.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            clsid.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);
            using (var iconKey = clsid.CreateSubKey("DefaultIcon"))
            {
                iconKey.SetValue("", icon, RegistryValueKind.ExpandString);
            }
            using (var server = clsid.CreateSubKey("InProcServer32"))
            {
                server.SetValue("", @"%SystemRoot%\System32\shell32.dll", RegistryValueKind.ExpandString);
            }
            using (var instance = clsid.CreateSubKey("Instance"))
            {
                instance.SetValue("CLSID", "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}"); // shell file system folder
                using var bag = instance.CreateSubKey("InitPropertyBag");
                bag.SetValue("Attributes", 0x11, RegistryValueKind.DWord);
                bag.SetValue("TargetFolderPath", targetPath, RegistryValueKind.ExpandString);
            }
            using (var folder = clsid.CreateSubKey("ShellFolder"))
            {
                folder.SetValue("FolderValueFlags", 0x28, RegistryValueKind.DWord);
                folder.SetValue("Attributes", unchecked((int)0xF080004D), RegistryValueKind.DWord);
            }
        }
        using (var nameSpace = Registry.CurrentUser.CreateSubKey(NameSpace))
        {
            nameSpace.SetValue("", name);
        }
        using (var hide = Registry.CurrentUser.CreateSubKey(HideFromDesktop))
        {
            hide.SetValue(Clsid, 1, RegistryValueKind.DWord);
        }
        Refresh();
    }

    public static void Unregister() => Unregister(Registry.CurrentUser, includeDriveLabels: false);

    /// <summary>Removes the navigation pane entry from a user's hive, and with <paramref name="includeDriveLabels"/> the drive names too (uninstall).</summary>
    public static void Unregister(RegistryKey hive, bool includeDriveLabels)
    {
        hive.DeleteSubKeyTree($@"{Classes}\CLSID\{Clsid}", false);
        hive.DeleteSubKeyTree($@"{Classes}\WOW6432Node\CLSID\{Clsid}", false);
        hive.DeleteSubKeyTree(NameSpace, false);
        using (var hide = hive.OpenSubKey(HideFromDesktop, writable: true))
        {
            hide?.DeleteValue(Clsid, false);
        }
        if (includeDriveLabels)
        {
            using var mountPoints = hive.OpenSubKey($@"{Explorer}\MountPoints2", writable: true);
            foreach (var name in DriveLabelKeys(mountPoints))
            {
                mountPoints!.DeleteSubKeyTree(name, false);
            }
        }
        Refresh();
    }

    public static bool IsRegistered(RegistryKey hive)
    {
        using var nameSpace = hive.OpenSubKey(NameSpace);
        using var mountPoints = hive.OpenSubKey($@"{Explorer}\MountPoints2");
        return nameSpace is not null || DriveLabelKeys(mountPoints).Any();
    }

    static IEnumerable<string> DriveLabelKeys(RegistryKey? mountPoints) =>
        mountPoints?.GetSubKeyNames().Where(name => name.StartsWith("##VFS365#", StringComparison.OrdinalIgnoreCase)) ?? [];

    /// <summary>
    /// The name Explorer shows for the network drive (instead of "share (\\server)"), for the UNC prefix \server\share.
    /// Drops names left for other prefixes, such as \VFS365\&lt;tenant&gt; from 0.1.3 and earlier.
    /// </summary>
    public static void SetDriveLabel(string uncPrefix, string label)
    {
        var keyName = uncPrefix.Replace('\\', '#').Insert(0, "#");
        using var mountPoints = Registry.CurrentUser.CreateSubKey($@"{Explorer}\MountPoints2");
        foreach (var stale in DriveLabelKeys(mountPoints).Where(name => !name.Equals(keyName, StringComparison.OrdinalIgnoreCase)))
        {
            mountPoints.DeleteSubKeyTree(stale, false);
        }
        using var mountPoint = mountPoints.CreateSubKey(keyName);
        mountPoint.SetValue("_LabelFromReg", label);
        Refresh();
    }

    static void Refresh() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
