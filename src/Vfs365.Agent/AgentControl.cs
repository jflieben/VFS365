using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vfs365.Agent;

/// <summary>
/// The stop signal of a running mount: one event per Windows session in the global namespace, so the uninstaller (SYSTEM) can reach
/// every user's agent. Only that user, SYSTEM and administrators may set it.
/// </summary>
public static class AgentControl
{
    public const string Notice = "Uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos, https://github.com/winfsp/winfsp";

    /// <summary>First line of the log, the console and About.</summary>
    public const string Publisher = "VFS365 is made by JSolve B.V., https://jsolve.nl";

    public static string Banner =>
        $"VFS365 {typeof(AgentControl).Assembly.GetName().Version!.ToString(3)}, free software under the GNU GPLv3. {Notice}";

    static int CurrentSession => Process.GetCurrentProcess().SessionId;

    static string EventName(int sessionId) => $@"Global\VFS365.Stop.{sessionId}";

    /// <summary>Created by a mount; createdNew is false when one is already running in this session.</summary>
    public static EventWaitHandle CreateStopEvent(out bool createdNew)
    {
        var security = new EventWaitHandleSecurity();
        foreach (var sid in new[]
        {
            WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        })
        {
            security.AddAccessRule(new EventWaitHandleAccessRule(sid, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        }
        return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, EventName(CurrentSession), out createdNew, security);
    }

    /// <summary>True while a mount runs in this session.</summary>
    public static bool IsMounted()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(CurrentSession), out var stop))
        {
            return false;
        }
        stop.Dispose();
        return true;
    }

    /// <summary>Asks the mount in a session (default: this one) to stop. False when none runs there.</summary>
    public static bool RequestStop(int? sessionId = null)
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(sessionId ?? CurrentSession), out var stop))
        {
            return false;
        }
        using (stop)
        {
            stop.Set();
        }
        return true;
    }
}
