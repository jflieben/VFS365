using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Vfs365.Agent;

/// <summary>
/// The stop signal of a running mount: one event per Windows session, in that session's own namespace (Local\), where only the
/// session's user, SYSTEM and administrators can create objects. The uninstaller (SYSTEM, session 0) reaches it through
/// Global\Session\&lt;id&gt;\ and sets it only when the agent's user owns it. Only that user, SYSTEM and administrators may set it.
/// </summary>
public static class AgentControl
{
    public const string Notice = "Uses WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos, https://github.com/winfsp/winfsp";

    /// <summary>First line of the log, the console and About.</summary>
    public const string Publisher = "VFS365 is made by JSolve B.V., https://jsolve.nl";

    public static string Banner =>
        $"VFS365 {typeof(AgentControl).Assembly.GetName().Version!.ToString(3)}, free software under the GNU GPLv3. {Notice}";

    const string Name = "VFS365.Stop";

    static int CurrentSession => Process.GetCurrentProcess().SessionId;

    /// <summary>The event's name as seen from <paramref name="callerSession"/>: Local\ in its own session, else through the session links.</summary>
    public static string EventName(int sessionId, int callerSession) =>
        sessionId == callerSession ? $@"Local\{Name}" : $@"Global\Session\{sessionId}\{Name}";

    /// <summary>Up to 0.3.2 the event lived in the global namespace, where any user can create it.</summary>
    static string LegacyEventName(int sessionId) => $@"Global\VFS365.Stop.{sessionId}";

    /// <summary>Agents before this version use <see cref="LegacyEventName"/>.</summary>
    public static readonly Version FirstSessionEvent = new(0, 4, 0);

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
        return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, EventName(CurrentSession, CurrentSession), out createdNew, security);
    }

    /// <summary>True while a mount runs in this session.</summary>
    public static bool IsMounted()
    {
        if (!EventWaitHandle.TryOpenExisting(EventName(CurrentSession, CurrentSession), out var stop))
        {
            return false;
        }
        stop.Dispose();
        return true;
    }

    /// <summary>Asks the mount in this session to stop. False when none runs here.</summary>
    public static bool RequestStop()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(EventName(CurrentSession, CurrentSession), out var stop))
            {
                return false;
            }
            using (stop)
            {
                stop.Set();
            }
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Asks the mount in another session to stop (machine-stop, as SYSTEM): sets its event only when <paramref name="user"/>, the user
    /// the session's agent runs as, owns it. Whoever can create objects in that namespace could otherwise plant the event, or a symbolic
    /// link that points SYSTEM at another event. <paramref name="legacy"/> (an agent before 0.4.0 runs there) also tries the old global name.
    /// </summary>
    public static bool RequestStop(int sessionId, SecurityIdentifier user, bool legacy) =>
        SetIfOwnedBy(EventName(sessionId, CurrentSession), user) || (legacy && SetIfOwnedBy(LegacyEventName(sessionId), user));

    /// <summary>Sets the named event when it exists and <paramref name="user"/> owns it; false otherwise.</summary>
    public static bool SetIfOwnedBy(string name, SecurityIdentifier user)
    {
        try
        {
            if (!EventWaitHandleAcl.TryOpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.ReadPermissions, out var stop))
            {
                return false;
            }
            using (stop)
            {
                if (stop.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier != user)
                {
                    return false;
                }
                stop.Set();
                return true;
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return false;
        }
    }
}
