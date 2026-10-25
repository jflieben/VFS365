using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>What the machine needs for the drive: WinFsp, both network providers in the order; the event source is information only.</summary>
public sealed record MachineHealth(string? WinFspVersion, IReadOnlyList<string> ProviderOrder, bool EventSource)
{
    public static MachineHealth Read() => new(WinFsp(), NetworkProviderOrder.Current(), WindowsEventSink.Registered());

    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (WinFspVersion is null)
        {
            problems.Add("WinFsp is not installed, so the drive can't start: install it (the VFS365 setup does)");
        }
        if (!ProviderOrder.Contains(NetworkProviderOrder.WinFsp, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{NetworkProviderOrder.WinFsp} is missing from the network provider order, so drive letters show as disconnected: put it back in " +
                "the policy that sets the order, or repair WinFsp");
        }
        if (!ProviderOrder.Contains(NetworkProviderOrder.Name, StringComparer.OrdinalIgnoreCase))
        {
            problems.Add($"{NetworkProviderOrder.Name} is missing from the network provider order, so typed \\\\VFS365 paths don't open: run 'vfs365.exe machine-setup' as an administrator");
        }
        return problems;
    }

    /// <summary>The version of WinFsp's DLL for this machine's architecture, from its install folder; null when it isn't installed.</summary>
    static string? WinFsp()
    {
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = hive.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key?.GetValue("InstallDir") is not string folder)
            {
                return null;
            }
            var arch = RuntimeInformation.OSArchitecture switch { Architecture.Arm64 => "a64", Architecture.X86 => "x86", _ => "x64" };
            var dll = Path.Combine(folder, "bin", $"winfsp-{arch}.dll");
            return File.Exists(dll) ? FileVersionInfo.GetVersionInfo(dll).FileVersion ?? "unknown version" : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// <summary>One user's agent: its status file (null when there is none) and whether an agent process runs in the session.</summary>
public sealed record UserHealth(string User, int Session, AgentStatus? Status, bool AgentRunning, IReadOnlyList<string> Problems);

public sealed record HealthReport(bool Healthy, string Summary, DateTimeOffset CheckedAt, MachineHealth Machine, IReadOnlyList<string> MachineProblems,
    IReadOnlyList<UserHealth> Users)
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        Healthy,
        Summary,
        CheckedAt,
        Machine = new { Machine.WinFspVersion, ProviderOrder = string.Join(",", Machine.ProviderOrder), Machine.EventSource, Problems = MachineProblems },
        Users = Users.Select(u => new
        {
            u.User,
            u.Session,
            Healthy = u.Problems.Count == 0,
            u.Problems,
            u.AgentRunning,
            State = u.Status?.State,
            u.Status?.Version,
            u.Status?.DriveLetter,
            u.Status?.Unc,
            SignedIn = u.Status?.User,
            u.Status?.SignInProblem,
            u.Status?.Online,
            u.Status?.Throttled,
            u.Status?.WaitingUploads,
            u.Status?.HeldChanges,
            u.Status?.UpdatedAt,
        }),
    }, Json);

    public IEnumerable<string> ToText()
    {
        yield return $"Health:     {(Healthy ? "healthy" : "unhealthy")}";
        yield return $"WinFsp:     {Machine.WinFspVersion ?? "not installed"}";
        yield return $"Providers:  {string.Join(",", Machine.ProviderOrder)}";
        yield return $"Events:     source {WindowsEventSink.Source} {(Machine.EventSource ? "registered" : "not registered (the MSI registers it)")}";
        foreach (var problem in MachineProblems)
        {
            yield return $"Problem:    {problem}";
        }
        foreach (var user in Users)
        {
            var status = user.Status;
            yield return $"User:       {user.User}, session {user.Session}: " + (status is null ? "no status" :
                $"{status.State}{(status.DriveLetter is { } letter ? $" at {letter} and" : status.IsMounted ? " at" : "")}{(status.IsMounted ? $" {status.Unc}" : "")}" +
                $"{(status.User is null ? "" : $" as {status.User}")}, {(status.Online ? "online" : "offline")}{(status.Throttled ? ", throttled" : "")}, " +
                $"{status.WaitingUploads} waiting, {status.HeldChanges} held, version {status.Version}, updated {status.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            foreach (var problem in user.Problems)
            {
                yield return $"Problem:    {problem}";
            }
        }
    }
}

/// <summary>
/// `vfs365 health`: the machine checks and the signed-in user's agent (or, with --all-users as SYSTEM or administrator, the agents of
/// every signed-in session). Healthy means nothing below needs a fix; offline, throttled and waiting uploads are reported, not counted.
/// </summary>
public static class Health
{
    static readonly string[] AgentNames = ["vfs365-agent", "vfs365"];

    /// <summary>An agent that has just started (a sign-in, machine-start) gets this long to mount before that counts.</summary>
    public static readonly TimeSpan StartingGrace = TimeSpan.FromMinutes(5);

    /// <summary>What is wrong with one user's agent.</summary>
    public static IReadOnlyList<string> UserProblems(AgentStatus? status, bool agentRunning, DateTimeOffset now)
    {
        var problems = new List<string>();
        if (!agentRunning)
        {
            problems.Add(status is { State: AgentStatus.Stopped } ? "the agent was stopped (it starts at the next sign-in, or from VFS365 in the Start menu)"
                : "no agent runs in this session (it starts at sign-in; 'vfs365.exe machine-start' as SYSTEM starts it)");
        }
        if (status is null)
        {
            problems.Add("no status file: the agent didn't run in this profile yet, or it is a version before health reporting");
            return problems;
        }
        if (agentRunning && now - status.UpdatedAt > StatusFile.Stale)
        {
            problems.Add($"the status wasn't updated for {(int)(now - status.UpdatedAt).TotalMinutes} minutes: the agent may hang");
        }
        if (agentRunning && !status.IsMounted && !(status.State == AgentStatus.Starting && now - status.StartedAt < StartingGrace))
        {
            problems.Add($"the drive is {status.State}{(status.Problem is { } why ? $": {why}" : "")}");
        }
        if (status.SignInProblem is { } code)
        {
            problems.Add($"sign-in needed ({code}); the user chooses Sign in in the tray");
        }
        return problems;
    }

    public static HealthReport Build(MachineHealth machine, IReadOnlyList<UserHealth> users, DateTimeOffset now)
    {
        var machineProblems = machine.Problems();
        var all = machineProblems.Concat(users.SelectMany(u => u.Problems.Select(p => $"{u.User}: {p}"))).ToList();
        var summary = all.Count == 0
            ? $"VFS365 healthy: {(users.Count == 0 ? "no user signed in" : $"{users.Count} user(s), WinFsp {machine.WinFspVersion}")}"
            : $"VFS365 unhealthy: {string.Join("; ", all)}";
        return new HealthReport(all.Count == 0, summary, now, machine, machineProblems, users);
    }

    /// <summary>The user running this command, in this session.</summary>
    public static HealthReport CurrentUser(DateTimeOffset now, string? statusPath = null)
    {
        using var process = Process.GetCurrentProcess();
        var session = process.SessionId;
        var status = StatusFile.Read(statusPath ?? StatusFile.DefaultPath);
        var running = AgentRuns(session, status);
        return Build(MachineHealth.Read(), [new UserHealth($@"{Environment.UserDomainName}\{Environment.UserName}", session, status, running,
            UserProblems(status, running, now))], now);
    }

    /// <summary>Every signed-in session's agent (active or disconnected, not session 0). Needs SYSTEM or an elevated administrator.</summary>
    public static HealthReport AllUsers(DateTimeOffset now)
    {
        var profiles = MachineCleanup.ProfileDataFolders().ToList();
        var users = new List<UserHealth>();
        foreach (var session in MachineStart.SessionsToStart(MachineStart.Sessions(), new HashSet<int>()))
        {
            var (name, sid) = SessionUser(session);
            if (name is null)
            {
                continue; // a session nobody is signed in to (the logon screen)
            }
            sid ??= AgentUser(session);
            var profile = sid is null ? null : profiles.FirstOrDefault(p => p.User == sid);
            var status = profile is null ? null : MachineCleanup.ReadDataFile(profile, StatusFile.FileName) is { } json ? StatusFile.Parse(json) : null;
            var running = AgentRuns(session, status);
            users.Add(new UserHealth(name, session, status, running, UserProblems(status, running, now)));
        }
        return Build(MachineHealth.Read(), users, now);
    }

    public static bool Privileged()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.IsSystem || new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>An agent process in the session: the one the status names, or any vfs365-agent.exe there.</summary>
    static bool AgentRuns(int session, AgentStatus? status)
    {
        var running = false;
        foreach (var process in AgentNames.SelectMany(Process.GetProcessesByName))
        {
            using (process)
            {
                running |= process.SessionId == session && (process.ProcessName.Equals(AgentNames[0], StringComparison.OrdinalIgnoreCase) || process.Id == status?.ProcessId);
            }
        }
        return running;
    }

    /// <summary>DOMAIN\name and SID of the user signed in to a session; null name for a session without a user.</summary>
    static (string? Name, SecurityIdentifier? Sid) SessionUser(int session)
    {
        var user = SessionText(session, 5); // WTSUserName
        if (string.IsNullOrEmpty(user))
        {
            return (null, null);
        }
        var name = SessionText(session, 7) is { Length: > 0 } domain ? $@"{domain}\{user}" : user;
        try
        {
            return (name, (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier)));
        }
        catch (Exception e) when (e is SystemException)
        {
            return (name, null);
        }
    }

    /// <summary>The user an agent in the session runs as, for names Windows can't translate.</summary>
    static SecurityIdentifier? AgentUser(int session)
    {
        SecurityIdentifier? user = null;
        foreach (var process in Process.GetProcessesByName(AgentNames[0]))
        {
            using (process)
            {
                if (user is null && process.SessionId == session)
                {
                    user = MachineCleanup.Describe(process, AppContext.BaseDirectory)?.User;
                }
            }
        }
        return user;
    }

    static string? SessionText(int session, int infoClass)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, session, infoClass, out var buffer, out _))
        {
            return null;
        }
        try
        {
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);
}
