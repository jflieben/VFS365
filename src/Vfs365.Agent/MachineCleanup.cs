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

    /// <summary>
    /// Signals every running agent, waits up to <paramref name="timeout"/> for them to finish uploading and unmount, then ends the rest.
    /// Only processes of the programs in this program's folder (the install folder) count: another program under the same name is left
    /// alone. A session's stop event counts only when the user its agent runs as owns it (<see cref="AgentControl.RequestStop(int, SecurityIdentifier, bool)"/>).
    /// </summary>
    public static void StopAgents(TimeSpan timeout, bool dryRun, Action<string> say)
    {
        var installFolder = AppContext.BaseDirectory;
        var agents = new List<(Process Process, (SecurityIdentifier User, Version Version)? Found)>();
        foreach (var process in AgentNames.SelectMany(Process.GetProcessesByName).Where(p => p.Id != Environment.ProcessId))
        {
            if (Describe(process, installFolder) is { } described)
            {
                agents.Add((process, described.User is null ? null : (described.User, described.Version)));
            }
            else
            {
                say($"Leaving {process.ProcessName} ({process.Id}) in session {process.SessionId}: not VFS365's program in {installFolder}");
            }
        }
        foreach (var session in agents.GroupBy(a => a.Process.SessionId))
        {
            if (dryRun)
            {
                say($"Would stop the agent in session {session.Key}");
                continue;
            }
            var found = session.Select(a => a.Found).OfType<(SecurityIdentifier User, Version Version)>().ToList();
            var legacy = found.Any(a => a.Version < AgentControl.FirstSessionEvent);
            say(found.Select(a => a.User).Distinct().Any(user => AgentControl.RequestStop(session.Key, user, legacy))
                ? $"Asked the agent in session {session.Key} to stop"
                : $"No stop signal of the agent's user in session {session.Key}");
        }
        if (dryRun)
        {
            return;
        }

        var deadline = DateTime.UtcNow + timeout;
        foreach (var agent in agents.Select(a => a.Process))
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

    /// <summary>
    /// An agent process whose program is in <paramref name="installFolder"/>: the user it runs as (null when it can't be read) and the
    /// version of that program, read from the install folder (never from a path a user chose: this runs as SYSTEM). Null for a process
    /// of another program, or one whose program can't be read.
    /// </summary>
    internal static (SecurityIdentifier? User, Version Version)? Describe(Process agent, string installFolder)
    {
        var process = OpenProcess(0x1000, false, agent.Id); // query limited information
        if (process == IntPtr.Zero)
        {
            return null;
        }
        try
        {
            var path = new StringBuilder(1024);
            var length = path.Capacity;
            if (!QueryFullProcessImageNameW(process, 0, path, ref length) || !InFolder(path.ToString(), installFolder))
            {
                return null;
            }
            SecurityIdentifier? user = null;
            if (OpenProcessToken(process, 0x8, out var token)) // query
            {
                try
                {
                    using var identity = new WindowsIdentity(token);
                    user = identity.User;
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            var file = FileVersionInfo.GetVersionInfo(Path.Combine(installFolder, Path.GetFileName(path.ToString())));
            return (user, new Version(file.FileMajorPart, file.FileMinorPart, file.FileBuildPart));
        }
        catch (Exception e) when (e is ArgumentException or System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>True when <paramref name="program"/> is a file right in <paramref name="folder"/>.</summary>
    internal static bool InFolder(string program, string folder) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(program)), Path.GetFullPath(folder).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);

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
    /// the verified handles, not paths. A failure in one profile is reported and the others go on.
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
            catch (Exception e)
            {
                // Whatever happens in one user's folder, the others are cleaned
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

    const string ExplorerKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
    const string PolicyKey = @"SOFTWARE\Policies\JSolve\VFS365";

    /// <summary>
    /// Puts back known folders VFS365 left in the volume (an agent ended after 60 s, a crash, a stop before the mount, a restore that
    /// failed) for every signed-in user (hive loaded under HKEY_USERS): each User Shell Folders value on \\VFS365\ or on the user's
    /// VFS365 drive letter goes back to where VFS365 found it (the profile's redirection.json), else to Windows' default. Shell Folders
    /// follows. Runs as SYSTEM in keys the user controls: they are opened one at a time without following registry links, values are
    /// compared as text and never expanded or opened (a path on \\VFS365 would send the computer's credentials to whoever answers that
    /// name), and redirection.json is read through the checked data folder. Users who are signed out get the same at their next sign-in
    /// (<see cref="LogonCleanupCommand"/>). Run before <see cref="RemoveUserData"/>, which removes redirection.json.
    /// </summary>
    public static void RestoreKnownFolders(IEnumerable<UserProfile> profiles, bool dryRun, Action<string> say)
    {
        var machineLetter = PolicyLetter(Registry.LocalMachine, say);
        foreach (var profile in profiles)
        {
            try
            {
                using var hive = Registry.Users.OpenSubKey(profile.User.Value);
                if (hive is null)
                {
                    continue; // signed out: the logon cleanup does it
                }
                var state = ReadState(profile);
                var letters = state.Select(m => m.Target).Where(t => t.Length >= 2 && t[1] == ':').Select(t => t[..2].ToUpperInvariant())
                    .Append(machineLetter ?? PolicyLetter(hive, say) ?? "").Where(l => l.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                RestoreKnownFolders(hive, profile.Folder, state, letters, dryRun, line => say($"{profile.User}: {line}"));
            }
            catch (Exception e)
            {
                // Whatever happens in one user's registry, the others are put back
                say($"Could not check the folders of {profile.User}: {e.Message}");
            }
        }
    }

    /// <summary>The folders of one user's <paramref name="hive"/>. Returns how many values were reset.</summary>
    internal static int RestoreKnownFolders(RegistryKey hive, string profileFolder, IReadOnlyList<FolderRedirection.Moved> state, IReadOnlySet<string> letters,
        bool dryRun, Action<string> say)
    {
        var reset = 0;
        foreach (var (keyName, expanded) in new[] { ("User Shell Folders", false), ("Shell Folders", true) })
        {
            using var key = NoFollowRegistry.Open(hive, $@"{ExplorerKey}\{keyName}", writable: !dryRun, say);
            if (key is null)
            {
                continue;
            }
            foreach (var name in key.GetValueNames())
            {
                if (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value || !InVfs365(value, letters))
                {
                    continue;
                }
                var replacement = Replacement(name, value, state, profileFolder, letters);
                if (expanded && replacement is not null)
                {
                    replacement = replacement.Replace("%USERPROFILE%", profileFolder, StringComparison.OrdinalIgnoreCase);
                }
                say($"{(dryRun ? "Would set" : "Setting")} {keyName}\\{name} from {value} to {replacement ?? "Windows' default (value removed)"}");
                reset++;
                if (dryRun)
                {
                    continue;
                }
                if (replacement is null)
                {
                    key.DeleteValue(name, false);
                }
                else
                {
                    key.SetValue(name, replacement, expanded ? RegistryValueKind.String : RegistryValueKind.ExpandString);
                }
            }
        }
        return reset;
    }

    /// <summary>A folder path on \\VFS365\ or on one of <paramref name="letters"/> ("M:").</summary>
    internal static bool InVfs365(string path, IReadOnlySet<string> letters) =>
        path.StartsWith(@"\\VFS365\", StringComparison.OrdinalIgnoreCase)
        || (path.Length >= 2 && path[1] == ':' && letters.Contains(path[..2]) && (path.Length == 2 || path[2] == '\\'));

    /// <summary>
    /// What a value in VFS365 goes back to: the original of the redirection.json record for that folder and target (with
    /// %USERPROFILE% for the profile), else Windows' default for the folder; null (remove the value: Windows' default) for other folders.
    /// </summary>
    static string? Replacement(string name, string value, IReadOnlyList<FolderRedirection.Moved> state, string profileFolder, IReadOnlySet<string> letters)
    {
        var known = KnownFolders.ShellFolderValues.FirstOrDefault(v => v.Value.Equals(name, StringComparison.OrdinalIgnoreCase));
        Guid? id = known.Value is not null ? known.Id : Guid.TryParse(name, out var parsed) ? parsed : null;
        var records = state.Where(m => m.Target.TrimEnd('\\').Equals(value.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) && !InVfs365(m.Original, letters)).ToList();
        if ((records.FirstOrDefault(m => id is { } folder && m.Ids.Contains(folder)) ?? (records.Count == 1 ? records[0] : null)) is { } moved)
        {
            var original = moved.Original.TrimEnd('\\');
            return original.StartsWith(profileFolder.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase)
                ? "%USERPROFILE%" + original[profileFolder.TrimEnd('\\').Length..]
                : original;
        }
        return known.Value is not null ? $@"%USERPROFILE%\{known.Default}" : null;
    }

    /// <summary>The profile's redirection.json, read through the checked data folder; empty when there is none or it can't be read.</summary>
    internal static IReadOnlyList<FolderRedirection.Moved> ReadState(UserProfile profile) =>
        ReadDataFile(profile, FolderRedirection.StateFileName) is { } json ? FolderRedirection.ParseState(json) : [];

    /// <summary>A small file of the profile's VFS365 folder, read through the checked data folder; null when it is missing, a link or can't be read.</summary>
    internal static string? ReadDataFile(UserProfile profile, string name)
    {
        try
        {
            using var data = NoFollow.OpenDataFolder(profile, _ => { }); // RemoveUserData reports a folder it skips
            return data is null ? null : NoFollow.ReadText(data, name, 1 << 20);
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The drive letter ("M:") a DriveLetter policy in <paramref name="root"/> (HKLM, or a user's hive) gives; null for none, * or no policy.</summary>
    static string? PolicyLetter(RegistryKey root, Action<string> say)
    {
        try
        {
            using var policy = NoFollowRegistry.Open(root, PolicyKey, writable: false, say);
            return DriveLetterOption.Parse(policy?.GetValue("DriveLetter", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string).MountPoint;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>Registry keys opened one at a time, never through a registry link (REG_OPTION_OPEN_LINK, then refused when it is one).</summary>
    static class NoFollowRegistry
    {
        const uint OpenLink = 0x8;
        const int QueryValue = 0x1, SetValue = 0x2, EnumerateSubKeys = 0x8, RegLink = 6;

        /// <summary><paramref name="path"/> below <paramref name="root"/>; null when a key is missing or a link (said why).</summary>
        public static RegistryKey? Open(RegistryKey root, string path, bool writable, Action<string> say)
        {
            SafeRegistryHandle? held = null;
            try
            {
                var names = path.Split('\\');
                for (var i = 0; i < names.Length; i++)
                {
                    var access = QueryValue | EnumerateSubKeys | (writable && i == names.Length - 1 ? SetValue : 0);
                    var error = RegOpenKeyExW(held ?? root.Handle, names[i], OpenLink, access, out var next);
                    if (error != 0)
                    {
                        next.Dispose();
                        return null;
                    }
                    held?.Dispose();
                    held = next;
                    var size = 0u;
                    if (RegQueryValueExW(held, "SymbolicLinkValue", IntPtr.Zero, out var type, IntPtr.Zero, ref size) == 0 && type == RegLink)
                    {
                        say($"Skipping {root.Name}\\{string.Join('\\', names[..(i + 1)])}: it is a registry link");
                        return null;
                    }
                }
                var key = RegistryKey.FromHandle(held!);
                held = null;
                return key;
            }
            finally
            {
                held?.Dispose();
            }
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyExW(SafeRegistryHandle key, string subKey, uint options, int access, out SafeRegistryHandle result);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved, out int type, IntPtr data, ref uint size);
    }

    /// <summary>
    /// Removes the Explorer entries from the registry of every signed-in user (their hives are loaded under HKEY_USERS), and registers
    /// an Active Setup component that removes them for everyone else at their next logon, in their own context, and resets their known
    /// folders left in VFS365 (<see cref="LogonCleanupCommand"/>). That reaches roaming and FSLogix profiles too and never loads a hive a
    /// user controls as SYSTEM.
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

        say($"{(dryRun ? "Would register" : "Registering")} the removal of Explorer entries and folders left in VFS365 at each user's next logon (Active Setup)");
        if (!dryRun)
        {
            try
            {
                var letter = PolicyLetter(Registry.LocalMachine, say);
                using var component = Registry.LocalMachine.CreateSubKey(ActiveSetupKey);
                component.SetValue("", "VFS365 removal");
                // Not expandable: Windows would expand %...% pairs in the command, which uses % for itself
                component.SetValue("StubPath", LogonCleanupCommand(letter is null ? [] : [letter]), RegistryValueKind.String);
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

    /// <summary>
    /// What ExplorerLocation.Unregister removes, every drive name (##VFS365#... in MountPoints2: the drive's and DriveMappings'), and each
    /// User Shell Folders value left on \\VFS365\ or on <paramref name="letters"/> ("M:") reset to Windows' default; with built-in tools
    /// only (VFS365 is gone by then). <paramref name="root"/>: the hive (HKCU; a key of their own for tests).
    /// </summary>
    public static string LogonCleanupCommand(IReadOnlyCollection<string> letters, string root = "HKCU")
    {
        var system = Environment.SystemDirectory;
        var reg = Path.Combine(system, "reg.exe");
        var findstr = Path.Combine(system, "findstr.exe");
        var explorer = $@"{root}\{ExplorerKey}";
        var clsid = ExplorerLocation.Clsid;
        // findstr patterns for a value's data in reg's output ("    Personal    REG_EXPAND_SZ    \\VFS365\ann\..."); \\\\ is one \ there
        var inVfs365 = string.Concat(letters.Select(l => $@"{l}\\\\").Prepend(@"\\\\VFS365\\\\").Select(p => $@" /c:""_SZ    {p}"""));
        var defaults = string.Join(" ", KnownFolders.ShellFolderValues.Select(v => $@"""{v.Value}={v.Default}"""));
        string[] steps =
        [
            .. ExplorerVerbs.RegPaths(root).Select(menu => $@"""{reg}"" delete ""{menu}"" /f >nul 2>&1"),
            $@"""{reg}"" delete ""{root}\Software\Classes\CLSID\{clsid}"" /f >nul 2>&1",
            $@"""{reg}"" delete ""{root}\Software\Classes\WOW6432Node\CLSID\{clsid}"" /f >nul 2>&1",
            $@"""{reg}"" delete ""{explorer}\Desktop\NameSpace\{clsid}"" /f >nul 2>&1",
            $@"""{reg}"" delete ""{explorer}\HideDesktopIcons\NewStartPanel"" /v ""{clsid}"" /f >nul 2>&1",
            $@"(for /f ""delims="" %k in ('{reg} query ""{explorer}\MountPoints2"" /f ##VFS365# /k 2^>nul ^| {findstr} /i /c:""\##VFS365#""') do ""{reg}"" delete ""%k"" /f >nul 2>&1)",
            // %^USERPROFILE% outside quotes: cmd removes the caret but doesn't expand it, so the value says %USERPROFILE% as Windows' own do
            $@"(for %v in ({defaults}) do for /f ""tokens=1,2 delims=="" %a in (%v) do (""{reg}"" query ""{explorer}\User Shell Folders"" /v ""%a"" 2>nul | " +
                $@"""{findstr}"" /i /r{inVfs365} >nul && ""{reg}"" add ""{explorer}\User Shell Folders"" /v ""%a"" /t REG_EXPAND_SZ /d %^USERPROFILE%""\%b"" /f >nul 2>&1))",
        ];
        return $@"""{Path.Combine(system, "cmd.exe")}"" /d /q /s /c ""{string.Join(" & ", steps)}""";
    }

    /// <summary>Opens and deletes through handles, never following reparse points (ntdll relative opens).</summary>
    static class NoFollow
    {
        const uint ListDirectory = 0x1, ReadData = 0x1, ReadAttributes = 0x80, DeleteAccess = 0x10000, ReadControl = 0x20000, Synchronize = 0x100000;
        const uint DirectoryFile = 0x1, SynchronousIo = 0x20, NonDirectoryFile = 0x40, OpenForBackup = 0x4000, OpenReparsePoint = 0x200000;
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

        /// <summary>A small file's text in the folder <paramref name="parent"/> is open on; null when it is missing, a link or larger than <paramref name="maxBytes"/>.</summary>
        public static string? ReadText(SafeFileHandle parent, string name, int maxBytes)
        {
            SafeFileHandle file;
            try
            {
                file = Open(parent, name, ReadData | ReadAttributes, NonDirectoryFile);
            }
            catch (Win32Exception e) when (e.NativeErrorCode is 2 or 3)
            {
                return null;
            }
            using (file)
            {
                if ((Attributes(file) & ReparsePointAttribute) != 0)
                {
                    return null;
                }
                using var stream = new FileStream(file, FileAccess.Read, 1, isAsync: false);
                if (stream.Length > maxBytes)
                {
                    return null;
                }
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
        }

        /// <summary>Folders deeper than this below the data folder are left (and named), however deep a user made them.</summary>
        const int MaxDepth = 64;

        /// <summary>
        /// Deletes an entry of <paramref name="parent"/> and, for a folder, its contents: depth first on a stack of open folders rather than
        /// by recursion, so a deep folder chain can't overflow the stack. Links and folders deeper than <see cref="MaxDepth"/> are left in
        /// place, and so are the folders holding them.
        /// </summary>
        public static void Delete(SafeFileHandle parent, string name, string display, Action<string> say)
        {
            if (Entry.Open(parent, name, display, 1, say) is not { } first)
            {
                return;
            }
            var open = new Stack<Entry>();
            open.Push(first);
            try
            {
                while (open.TryPeek(out var top))
                {
                    if (top.Children.TryDequeue(out var child))
                    {
                        var path = Path.Combine(top.Display, child);
                        try
                        {
                            if (Entry.Open(top.Listing!, child, path, open.Count + 1, say) is { } entry)
                            {
                                open.Push(entry);
                                continue;
                            }
                        }
                        catch (Win32Exception e)
                        {
                            say($"Could not remove {path}: {e.Message}");
                        }
                        top.Kept = true;
                        continue;
                    }
                    open.Pop();
                    using (top)
                    {
                        if (top.Kept)
                        {
                            if (open.TryPeek(out var up))
                            {
                                up.Kept = true;
                            }
                            continue;
                        }
                        if (open.Count == 0)
                        {
                            MarkDeleted(top.Handle); // the caller reports a failure of the entry itself
                            continue;
                        }
                        try
                        {
                            MarkDeleted(top.Handle);
                        }
                        catch (Win32Exception e)
                        {
                            say($"Could not remove {top.Display}: {e.Message}");
                            open.Peek().Kept = true;
                        }
                    }
                }
            }
            finally
            {
                while (open.TryPop(out var left))
                {
                    left.Dispose();
                }
            }
        }

        /// <summary>An entry being deleted: its handle and, for a folder, a listing handle and the children not yet deleted.</summary>
        sealed class Entry(SafeFileHandle handle, SafeFileHandle? listing, Queue<string> children, string display) : IDisposable
        {
            public SafeFileHandle Handle => handle;
            public SafeFileHandle? Listing => listing;
            public Queue<string> Children => children;
            public string Display => display;

            /// <summary>Something below stays, so this folder stays too.</summary>
            public bool Kept { get; set; }

            /// <summary>Opens <paramref name="name"/> in <paramref name="parent"/> at <paramref name="depth"/>; null (and said why) for a link or a folder too deep.</summary>
            public static Entry? Open(SafeFileHandle parent, string name, string display, int depth, Action<string> say)
            {
                var entry = NoFollow.Open(parent, name, DeleteAccess | ReadAttributes, 0);
                try
                {
                    var attributes = Attributes(entry);
                    if ((attributes & ReparsePointAttribute) != 0)
                    {
                        say($"Leaving {display}: it is a link (reparse point)");
                    }
                    else if ((attributes & DirectoryAttribute) == 0)
                    {
                        return new Entry(entry, null, new Queue<string>(), display);
                    }
                    else if (depth > MaxDepth)
                    {
                        say($"Leaving {display}: more than {MaxDepth} folders deep");
                    }
                    else
                    {
                        // The same folder again (empty name), for listing
                        var folder = NoFollow.Open(entry, "", ListDirectory, DirectoryFile);
                        try
                        {
                            return new Entry(entry, folder, new Queue<string>(List(folder)), display);
                        }
                        catch
                        {
                            folder.Dispose();
                            throw;
                        }
                    }
                }
                catch
                {
                    entry.Dispose();
                    throw;
                }
                entry.Dispose();
                return null;
            }

            public void Dispose()
            {
                listing?.Dispose();
                handle.Dispose();
            }
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
