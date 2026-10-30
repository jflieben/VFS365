using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>
/// Explorer integration, all per user (HKCU, no admin rights): a navigation pane node (delegate folder) that shows the drive or UNC path,
/// registered in the 64-bit and 32-bit views so file dialogs of both kinds show it, and the names Explorer gives the network drive and
/// the drive letters for folders (<see cref="SessionDriveLetters"/>) in MountPoints2.
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

    /// <summary>
    /// Removes the navigation pane entry from a user's hive, and with <paramref name="includeDriveLabels"/> the drive names, the VFS365
    /// menu (<see cref="ExplorerVerbs"/>) and the Office verb (<see cref="OfficeVerbs"/>) too (uninstall).
    /// </summary>
    public static void Unregister(RegistryKey hive, bool includeDriveLabels)
    {
        if (includeDriveLabels)
        {
            ExplorerVerbs.Remove(hive);
            OfficeVerbs.RemoveAll(hive);
        }
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
        return nameSpace is not null || DriveLabelKeys(mountPoints).Any() || ExplorerVerbs.IsRegistered(hive) || OfficeVerbs.IsRegistered(hive);
    }

    static IEnumerable<string> DriveLabelKeys(RegistryKey? mountPoints) =>
        mountPoints?.GetSubKeyNames().Where(name => name.StartsWith("##VFS365#", StringComparison.OrdinalIgnoreCase)) ?? [];

    /// <summary>
    /// The name Explorer shows for the network drive (instead of "share (\\server)"), for the UNC prefix \server\share.
    /// Drops names left for other prefixes, such as \VFS365\&lt;tenant&gt; from 0.1.3 and earlier. Explorer is told only when something changed.
    /// </summary>
    public static void SetDriveLabel(string uncPrefix, string label)
    {
        var keyName = LabelKey(uncPrefix);
        using var mountPoints = Registry.CurrentUser.CreateSubKey($@"{Explorer}\MountPoints2");
        var changed = false;
        foreach (var stale in DriveLabelKeys(mountPoints).Where(name => !name.Equals(keyName, StringComparison.OrdinalIgnoreCase)
            && !name.StartsWith(keyName + "#", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            mountPoints.DeleteSubKeyTree(stale, false);
            changed = true;
        }
        if (WriteLabel(mountPoints, keyName, label) || changed)
        {
            Refresh();
        }
    }

    /// <summary>
    /// The name Explorer shows for a drive letter given a folder inside the volume (policy DriveMappings), by its UNC path. Written only
    /// when it differs: true then, and the caller tells Explorer once (<see cref="Refresh"/>) after all its letters.
    /// </summary>
    public static bool SetFolderLabel(string unc, string label)
    {
        using var mountPoints = Registry.CurrentUser.CreateSubKey($@"{Explorer}\MountPoints2");
        return WriteLabel(mountPoints, LabelKey(unc), label);
    }

    static bool WriteLabel(RegistryKey mountPoints, string keyName, string label)
    {
        using (var existing = mountPoints.OpenSubKey(keyName))
        {
            if (existing?.GetValue("_LabelFromReg") is string current && current == label)
            {
                return false;
            }
        }
        using var mountPoint = mountPoints.CreateSubKey(keyName);
        mountPoint.SetValue("_LabelFromReg", label);
        return true;
    }

    /// <summary>Removes a folder's drive name (at unmount); a registry that refuses it never stops the unmount.</summary>
    public static void RemoveFolderLabel(string unc)
    {
        try
        {
            using var mountPoints = Registry.CurrentUser.OpenSubKey($@"{Explorer}\MountPoints2", writable: true);
            mountPoints?.DeleteSubKeyTree(LabelKey(unc), false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    /// <summary>MountPoints2's key for \\server\share\path (or \server\share): ##server#share#path.</summary>
    static string LabelKey(string unc) => "##" + unc.TrimStart('\\').Replace('\\', '#');

    /// <summary>Tells Explorer to read its folders and names again (SHCNE_ASSOCCHANGED).</summary>
    public static void Refresh() => SHChangeNotify(0x08000000, 0, null, IntPtr.Zero);

    /// <summary>Tells Explorer a drive letter ("Z:") came (SHCNE_DRIVEADD) or went (SHCNE_DRIVEREMOVED), by its path (SHCNF_PATHW).</summary>
    internal static void DriveAdded(string letter) => SHChangeNotify(0x100, 5, letter + @"\", IntPtr.Zero);

    internal static void DriveRemoved(string letter) => SHChangeNotify(0x80, 5, letter + @"\", IntPtr.Zero);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);
}
