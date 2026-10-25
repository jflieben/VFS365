using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Vfs365.Agent;

/// <summary>
/// What the MSI runs as SYSTEM at the end of an install or upgrade (`vfs365.exe machine-start`): starts vfs365-agent.exe as the
/// signed-in user in every user session that has none, so nobody waits for the next logon (the HKLM Run key) to get the drive back.
/// </summary>
public static class MachineStart
{
    const int Active = 0, Disconnected = 4; // WTS_CONNECTSTATE_CLASS

    /// <summary>
    /// Sessions to start an agent in: signed in (active, or disconnected as on AVD, where users often disconnect instead of signing
    /// out), not session 0 (services), and without a running agent.
    /// </summary>
    public static IEnumerable<int> SessionsToStart(IEnumerable<(int Id, int State)> sessions, IReadOnlySet<int> running) =>
        sessions.Where(s => s.Id != 0 && s.State is Active or Disconnected && !running.Contains(s.Id)).Select(s => s.Id).Distinct();

    public static void Run(bool dryRun, Action<string> say)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "vfs365-agent.exe");
        if (!File.Exists(exe))
        {
            say($"Not found: {exe}");
            return;
        }
        var running = Process.GetProcessesByName("vfs365-agent").Select(p => p.SessionId).ToHashSet();
        var sessions = SessionsToStart(Sessions(), running).ToList();
        if (sessions.Count == 0)
        {
            say("No signed-in session without an agent");
            return;
        }
        if (!dryRun)
        {
            // CreateProcessAsUser with another user's token; SYSTEM holds both but may have them disabled
            EnablePrivilege("SeAssignPrimaryTokenPrivilege");
            EnablePrivilege("SeIncreaseQuotaPrivilege");
        }
        foreach (var session in sessions)
        {
            if (dryRun)
            {
                say($"Would start the agent in session {session}");
                continue;
            }
            try
            {
                var id = StartAs(session, exe);
                say($"Started the agent in session {session} (process {id})");
            }
            catch (Win32Exception e)
            {
                say($"Could not start the agent in session {session}: {e.Message}{(e.NativeErrorCode == 1314 ? " (runs as SYSTEM only)" : "")}");
            }
        }
    }

    internal static List<(int Id, int State)> Sessions()
    {
        var sessions = new List<(int, int)>();
        if (!WTSEnumerateSessionsW(IntPtr.Zero, 0, 1, out var list, out var count))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<SessionInfo>(list + i * size);
                sessions.Add((info.SessionId, info.State));
            }
        }
        finally
        {
            WTSFreeMemory(list);
        }
        return sessions;
    }

    /// <summary>Starts the agent as the session's user, on its desktop, with that user's environment. Returns the process ID.</summary>
    static int StartAs(int session, string exe)
    {
        if (!WTSQueryUserToken(session, out var token))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        var environment = IntPtr.Zero;
        try
        {
            if (!CreateEnvironmentBlock(out environment, token, false))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = @"winsta0\default" };
            const uint unicodeEnvironment = 0x400, breakaway = 0x01000000;
            var directory = Path.GetDirectoryName(exe);
            // Out of the installer's job when it allows that, so ending the job never ends the agent
            if (!CreateProcessAsUserW(token, exe, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, false, unicodeEnvironment | breakaway, environment, directory, ref startup, out var process)
                && !CreateProcessAsUserW(token, exe, new StringBuilder($"\"{exe}\""), IntPtr.Zero, IntPtr.Zero, false, unicodeEnvironment, environment, directory, ref startup, out process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            CloseHandle(process.Thread);
            CloseHandle(process.Process);
            return process.ProcessId;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }
            CloseHandle(token);
        }
    }

    static void EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Process.GetCurrentProcess().Handle, 0x28, out var token)) // adjust privileges, query
        {
            return;
        }
        try
        {
            var privileges = new TokenPrivileges { Count = 1, Attributes = 2 }; // enabled
            if (LookupPrivilegeValueW(null, name, out privileges.Luid))
            {
                AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero);
            }
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SessionInfo
    {
        public int SessionId;
        public string? WinStationName;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2;
        public IntPtr Reserved3, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct TokenPrivileges
    {
        public int Count;
        public long Luid;
        public int Attributes;
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSEnumerateSessionsW(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSQueryUserToken(int sessionId, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll")]
    static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessAsUserW(IntPtr token, string application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref StartupInfo startup, out ProcessInformation process);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TokenPrivileges state, int length, IntPtr previous, IntPtr returned);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr handle);
}
