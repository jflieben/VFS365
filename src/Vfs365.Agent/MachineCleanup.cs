using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Vfs365.Agent;

/// <summary>
/// What the MSI runs as SYSTEM before it removes files (`vfs365.exe machine-stop`): stops every user's agent cleanly, and on a full
/// uninstall (--cleanup) removes per-user data and Explorer entries. Unsaved changes are kept and reported.
/// </summary>
public static class MachineCleanup
{
    static readonly string[] AgentNames = ["vfs365-agent", "vfs365"];

    /// <summary>%LOCALAPPDATA%\VFS365 below a profile folder.</summary>
    static readonly string[] DataPath = ["AppData", "Local", "VFS365"];

    /// <summary>Active Setup component that removes Explorer entries at each user's next logon. The MSI deletes it on install.</summary>
    public const string ActiveSetupKey = @"SOFTWARE\Microsoft\Active Setup\Installed Components\{76103053-0966-49F3-9EE8-923401C976B1}";

    /// <summary>A profile from ProfileList: its folder and its user.</summary>
    public sealed record UserProfile(string Folder, SecurityIdentifier User)
    {
        public string DataFolder => Path.Combine([Folder, .. DataPath]);
    }

    /// <summary>Signals every running agent, waits up to <paramref name="timeout"/> for them to finish uploading and unmount, then ends the rest.</summary>
    public static void StopAgents(TimeSpan timeout, bool dryRun, Action<string> say)
    {
        var agents = AgentNames.SelectMany(Process.GetProcessesByName).Where(p => p.Id != Environment.ProcessId).ToList();
        foreach (var session in agents.Select(p => p.SessionId).Distinct())
        {
            if (dryRun)
            {
                say($"Would stop the agent in session {session}");
            }
            else
            {
                say(AgentControl.RequestStop(session) ? $"Asked the agent in session {session} to stop" : $"No stop signal in session {session}");
            }
        }
        if (dryRun)
        {
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        foreach (var agent in agents)
        {
            var left = deadline - DateTime.UtcNow;
            if (left > TimeSpan.Zero && agent.WaitForExit(left))
            {
                continue;
            }
            say($"Ending {agent.ProcessName} ({agent.Id}) in session {agent.SessionId}; unsaved changes stay journaled");
            try
            {
                agent.Kill();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    /// <summary>Every profile on the machine, for its %LOCALAPPDATA%\VFS365.</summary>
    public static IEnumerable<UserProfile> ProfileDataFolders()
    {
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        foreach (var sid in profiles?.GetSubKeyNames() ?? [])
        {
            using var profile = profiles!.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is string path && ParseSid(sid) is { } user)
            {
                yield return new UserProfile(Environment.ExpandEnvironmentVariables(path), user);
            }
        }
    }

    // ProfileList also holds entries such as S-1-5-21-...bak (a profile Windows set aside)
    static SecurityIdentifier? ParseSid(string sid)
    {
        try
        {
            return new SecurityIdentifier(sid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Removes cached content, state and logs. A staging folder with journaled (unsaved) changes is kept.
    /// Runs as SYSTEM in folders the user controls, so it never follows a link: AppData, Local and VFS365 are opened one at a time, each
    /// relative to the handle of the one before, and refused when it is a reparse point (junction, symbolic link, mount point); VFS365
    /// must be owned by the profile's user, SYSTEM or Administrators. Everything below is opened relative to that folder's handle without
    /// following reparse points, which are left in place. Swapping a folder for a link after a check redirects nothing: opens go through
    /// the verified handles, not paths.
    /// </summary>
    public static void RemoveUserData(IEnumerable<UserProfile> profiles, bool dryRun, Action<string> say)
    {
        foreach (var profile in profiles)
        {
            var folder = profile.DataFolder;
            if (!Directory.Exists(folder))
            {
                continue;
            }
            try
            {
                using var data = NoFollow.OpenDataFolder(profile, say);
                if (data is not null)
                {
                    RemoveContents(data, folder, dryRun, say);
                }
            }
            catch (Win32Exception e)
            {
                say($"Could not clean {folder}: {e.Message}");
            }
        }
    }

    static void RemoveContents(SafeFileHandle data, string folder, bool dryRun, Action<string> say)
    {
        var staging = Path.Combine(folder, "staging");
        var unsaved = NoFollow.CountUnsaved(data, "staging");
        if (unsaved > 0)
        {
            say($"Keeping {unsaved} unsaved file(s) in {staging}");
        }
        foreach (var name in NoFollow.List(data).Where(n => unsaved == 0 || !n.Equals("staging", StringComparison.OrdinalIgnoreCase)))
        {
            var entry = Path.Combine(folder, name);
            say($"{(dryRun ? "Would remove" : "Removing")} {entry}");
            if (!dryRun)
            {
                try
                {
                    NoFollow.Delete(data, name, entry, say);
                }
                catch (Win32Exception e)
                {
                    say($"Could not remove {entry}: {e.Message}");
                }
            }
        }
        if (!dryRun && unsaved == 0 && NoFollow.List(data).Count == 0)
        {
            NoFollow.MarkDeleted(data);
        }
    }

    /// <summary>
    /// Removes the Explorer entries from the registry of every signed-in user (their hives are loaded under HKEY_USERS), and registers
    /// an Active Setup component that removes them for everyone else at their next logon, in their own context. That reaches roaming
    /// and FSLogix profiles too and never loads a hive a user controls as SYSTEM.
    /// </summary>
    public static void RemoveExplorerEntries(bool dryRun, Action<string> say)
    {
        foreach (var sid in Registry.Users.GetSubKeyNames().Where(s => !s.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var hive = Registry.Users.OpenSubKey(sid, writable: !dryRun);
                if (hive is not null && ExplorerLocation.IsRegistered(hive))
                {
                    say($"{(dryRun ? "Would remove" : "Removing")} the Explorer entries of {sid}");
                    if (!dryRun)
                    {
                        ExplorerLocation.Unregister(hive, includeDriveLabels: true);
                    }
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                say($"Could not check {sid}: {e.Message}");
            }
        }

        say($"{(dryRun ? "Would register" : "Registering")} the removal of Explorer entries at each user's next logon (Active Setup)");
        if (!dryRun)
        {
            try
            {
                using var component = Registry.LocalMachine.CreateSubKey(ActiveSetupKey);
                component.SetValue("", "VFS365 removal");
                component.SetValue("StubPath", LogonCleanupCommand(), RegistryValueKind.ExpandString);
                component.SetValue("Version", ActiveSetupVersion(DateTime.UtcNow));
                component.SetValue("IsInstalled", 1, RegistryValueKind.DWord);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                say($"Could not register the logon cleanup: {e.Message}");
            }
        }
    }

    /// <summary>Higher at every uninstall, so users who ran an earlier cleanup run it again.</summary>
    public static string ActiveSetupVersion(DateTime utc) => $"{utc.Year},{utc.Month * 100 + utc.Day},{utc.Hour * 100 + utc.Minute},{utc.Second}";

    /// <summary>What ExplorerLocation.Unregister removes, with built-in tools only (VFS365 is gone by then); the drive name is per user name.</summary>
    public static string LogonCleanupCommand()
    {
        const string reg = @"%SystemRoot%\System32\reg.exe";
        const string explorer = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer";
        var clsid = ExplorerLocation.Clsid;
        var deletes = new[]
        {
            $@"""{reg}"" delete ""HKCU\Software\Classes\CLSID\{clsid}"" /f",
            $@"""{reg}"" delete ""HKCU\Software\Classes\WOW6432Node\CLSID\{clsid}"" /f",
            $@"""{reg}"" delete ""{explorer}\Desktop\NameSpace\{clsid}"" /f",
            $@"""{reg}"" delete ""{explorer}\HideDesktopIcons\NewStartPanel"" /v ""{clsid}"" /f",
            $@"""{reg}"" delete ""{explorer}\MountPoints2\##VFS365#%USERNAME%"" /f",
        };
        return $@"%SystemRoot%\System32\cmd.exe /d /q /s /c ""{string.Join(" & ", deletes.Select(d => d + " >nul 2>&1"))}""";
    }

    /// <summary>Opens and deletes through handles, never following reparse points (ntdll relative opens).</summary>
    static class NoFollow
    {
        const uint ListDirectory = 0x1, ReadAttributes = 0x80, DeleteAccess = 0x10000, ReadControl = 0x20000, Synchronize = 0x100000;
        const uint DirectoryFile = 0x1, SynchronousIo = 0x20, OpenForBackup = 0x4000, OpenReparsePoint = 0x200000;
        const uint ReparsePointAttribute = 0x400, DirectoryAttribute = 0x10;

        /// <summary>The profile's VFS365 folder, checked; null (and said why) when it is a link or someone else's.</summary>
        public static SafeFileHandle? OpenDataFolder(UserProfile profile, Action<string> say)
        {
            // The profile folder comes from ProfileList (admins only) and may be a mount point, for example an FSLogix container
            var current = OpenPath(profile.Folder);
            var path = profile.Folder;
            for (var i = 0; i < DataPath.Length; i++)
            {
                path = Path.Combine(path, DataPath[i]);
                var last = i == DataPath.Length - 1;
                using (var parent = current)
                {
                    current = Open(parent, DataPath[i], ListDirectory | ReadAttributes | (last ? ReadControl | DeleteAccess : 0), DirectoryFile);
                }
                if ((Attributes(current) & ReparsePointAttribute) != 0)
                {
                    current.Dispose();
                    say($"Skipping {path}: it is a link (reparse point), not a folder");
                    return null;
                }
            }

            var owner = Owner(current);
            if (owner != profile.User && !owner.IsWellKnown(WellKnownSidType.LocalSystemSid) && !owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid))
            {
                current.Dispose();
                say($"Skipping {path}: owned by {owner}, not by the profile's user {profile.User}");
                return null;
            }
            return current;
        }

        /// <summary>Journaled (unsaved) changes in a subfolder; 0 when it is missing or a link.</summary>
        public static int CountUnsaved(SafeFileHandle data, string name)
        {
            try
            {
                using var folder = Open(data, name, ListDirectory | ReadAttributes, DirectoryFile);
                return (Attributes(folder) & ReparsePointAttribute) != 0
                    ? 0
                    : List(folder).Count(n => n.EndsWith(".staging.json", StringComparison.OrdinalIgnoreCase));
            }
            catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3 or 267) // not found, or not a folder
            {
                return 0;
            }
        }

        /// <summary>Deletes an entry of <paramref name="parent"/> and, for a folder, its contents. Links are left in place.</summary>
        public static void Delete(SafeFileHandle parent, string name, string display, Action<string> say)
        {
            using var entry = Open(parent, name, DeleteAccess | ReadAttributes, 0);
            var attributes = Attributes(entry);
            if ((attributes & ReparsePointAttribute) != 0)
            {
                say($"Leaving {display}: it is a link (reparse point)");
                return;
            }
            if ((attributes & DirectoryAttribute) != 0)
            {
                // The same folder again (empty name), for listing
                using var folder = Open(entry, "", ListDirectory, DirectoryFile);
                foreach (var child in List(folder))
                {
                    try
                    {
                        Delete(folder, child, Path.Combine(display, child), say);
                    }
                    catch (Win32Exception e)
                    {
                        say($"Could not remove {Path.Combine(display, child)}: {e.Message}");
                    }
                }
            }
            MarkDeleted(entry);
        }

        /// <summary>Deletes what the handle is open on (POSIX semantics: the name goes at once, read-only or not).</summary>
        public static void MarkDeleted(SafeFileHandle handle)
        {
            var flags = 0x1u | 0x2u | 0x10u; // delete, POSIX semantics, ignore read-only
            if (SetFileInformationByHandle(handle, 21, ref flags, sizeof(uint))) // FileDispositionInfoEx
            {
                return;
            }
            var error = Marshal.GetLastWin32Error();
            if (error is not (50 or 87)) // file systems without the extended form
            {
                throw new Win32Exception(error);
            }
            byte delete = 1;
            if (!SetFileInformationByHandle(handle, 4, ref delete, 1)) // FileDispositionInfo
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        public static List<string> List(SafeFileHandle folder)
        {
            var names = new List<string>();
            var buffer = new byte[64 * 1024];
            // FILE_FULL_DIR_INFO: NextEntryOffset at 0, FileNameLength at 60, FileName at 68. The restart class starts from the top.
            for (var infoClass = 15; GetFileInformationByHandleEx(folder, infoClass, buffer, buffer.Length); infoClass = 14)
            {
                for (var offset = 0; ;)
                {
                    var name = Encoding.Unicode.GetString(buffer, offset + 68, BitConverter.ToInt32(buffer, offset + 60));
                    if (name is not ("." or ".."))
                    {
                        names.Add(name);
                    }
                    var next = BitConverter.ToInt32(buffer, offset);
                    if (next == 0)
                    {
                        break;
                    }
                    offset += next;
                }
            }
            var error = Marshal.GetLastWin32Error();
            return error == 18 ? names : throw new Win32Exception(error); // ERROR_NO_MORE_FILES
        }

        static uint Attributes(SafeFileHandle handle)
        {
            var info = new byte[8]; // FILE_ATTRIBUTE_TAG_INFO
            return GetFileInformationByHandleEx(handle, 9, info, info.Length) ? BitConverter.ToUInt32(info, 0) : throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        static SecurityIdentifier Owner(SafeFileHandle handle)
        {
            var error = GetSecurityInfo(handle, 1, 1, out var owner, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out var descriptor); // file, owner
            if (error != 0)
            {
                throw new Win32Exception((int)error);
            }
            try
            {
                return new SecurityIdentifier(owner);
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        static SafeFileHandle OpenPath(string path)
        {
            var handle = CreateFileW(path, ListDirectory | ReadAttributes | Synchronize, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero); // open existing, backup semantics
            return handle.IsInvalid ? throw new Win32Exception(Marshal.GetLastWin32Error()) : handle;
        }

        /// <summary><paramref name="name"/> inside the folder <paramref name="parent"/> is open on; a reparse point opens as itself.</summary>
        static SafeFileHandle Open(SafeFileHandle parent, string name, uint access, uint options)
        {
            var text = Marshal.StringToHGlobalUni(name);
            var unicode = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            var added = false;
            try
            {
                parent.DangerousAddRef(ref added);
                Marshal.StructureToPtr(new UnicodeString { Length = (ushort)(name.Length * 2), MaximumLength = (ushort)(name.Length * 2 + 2), Buffer = text }, unicode, false);
                var attributes = new ObjectAttributes
                {
                    Length = Marshal.SizeOf<ObjectAttributes>(),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = unicode,
                    Attributes = 0x40, // case insensitive
                };
                var status = NtCreateFile(out var handle, access | Synchronize, ref attributes, out _, IntPtr.Zero, 0, 7, 1, // share all, open
                    options | SynchronousIo | OpenReparsePoint | OpenForBackup, IntPtr.Zero, 0);
                if (status < 0)
                {
                    handle.Dispose();
                    throw new Win32Exception(RtlNtStatusToDosError(status));
                }
                return handle;
            }
            finally
            {
                if (added)
                {
                    parent.DangerousRelease();
                }
                Marshal.FreeHGlobal(unicode);
                Marshal.FreeHGlobal(text);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct ObjectAttributes
        {
            public int Length;
            public IntPtr RootDirectory;
            public IntPtr ObjectName;
            public uint Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct IoStatusBlock
        {
            public IntPtr Status;
            public IntPtr Information;
        }

        [DllImport("ntdll.dll")]
        static extern int NtCreateFile(out SafeFileHandle handle, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
            IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr eaBuffer, uint eaLength);

        [DllImport("ntdll.dll")]
        static extern int RtlNtStatusToDosError(int status);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, byte[] buffer, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref uint info, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool SetFileInformationByHandle(SafeFileHandle handle, int infoClass, ref byte info, int size);

        [DllImport("advapi32.dll")]
        static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType, uint information, out IntPtr owner, IntPtr group, IntPtr dacl,
            IntPtr sacl, out IntPtr descriptor);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);
    }
}
