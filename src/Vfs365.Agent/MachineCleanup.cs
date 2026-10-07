using System.Diagnostics;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>
/// What the MSI runs as SYSTEM before it removes files (`vfs365.exe machine-stop`): stops every user's agent cleanly, and on a full
/// uninstall (--cleanup) removes per-user data and Explorer entries. Unsaved changes are kept and reported.
/// </summary>
public static class MachineCleanup
{
    static readonly string[] AgentNames = ["vfs365-agent", "vfs365"];

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

    /// <summary>%LOCALAPPDATA%\VFS365 of every profile on the machine.</summary>
    public static IEnumerable<string> ProfileDataFolders()
    {
        using var profiles = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
        foreach (var sid in profiles?.GetSubKeyNames() ?? [])
        {
            using var profile = profiles!.OpenSubKey(sid);
            if (profile?.GetValue("ProfileImagePath") is string path)
            {
                yield return Path.Combine(Environment.ExpandEnvironmentVariables(path), "AppData", "Local", "VFS365");
            }
        }
    }

    /// <summary>Removes cached content, state and logs. A staging folder with journaled (unsaved) changes is kept.</summary>
    public static void RemoveUserData(IEnumerable<string> dataFolders, bool dryRun, Action<string> say)
    {
        foreach (var folder in dataFolders.Where(Directory.Exists))
        {
            var staging = Path.Combine(folder, "staging");
            var unsaved = Directory.Exists(staging) ? Directory.GetFiles(staging, "*.staging.json").Length : 0;
            if (unsaved > 0)
            {
                say($"Keeping {unsaved} unsaved file(s) in {staging}");
            }
            foreach (var entry in Directory.EnumerateFileSystemEntries(folder).Where(e => unsaved == 0 || !e.Equals(staging, StringComparison.OrdinalIgnoreCase)))
            {
                say($"{(dryRun ? "Would remove" : "Removing")} {entry}");
                if (!dryRun)
                {
                    try
                    {
                        if (Directory.Exists(entry))
                        {
                            Directory.Delete(entry, recursive: true);
                        }
                        else
                        {
                            File.Delete(entry);
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        say($"Could not remove {entry}: {e.Message}");
                    }
                }
            }
            if (!dryRun && unsaved == 0 && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
    }

    /// <summary>Removes the Explorer entries from the registry of every signed-in user (their hives are loaded under HKEY_USERS).</summary>
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
    }
}
