using Microsoft.Win32;
using Vfs365.Agent;

// Started at sign-in, this is the supervisor: it runs the agent as a child process and starts it again after a crash. The child writes
// to %LOCALAPPDATA%\VFS365\agent.log, a new file per sign-in (a restart from the tray, with --after, appends). Logoff, shutdown and the
// installer (Restart Manager) end the session cleanly: the navigation pane entry goes at once, then the mount flushes and stops.
if (!args.Contains(AgentSupervisor.ChildFlag, StringComparer.OrdinalIgnoreCase))
{
    var ending = false;
    SystemEvents.SessionEnding += (_, _) => ending = true; // the child is ended at logoff too: no restart then
    return AgentSupervisor.Run(args, () => ending);
}
SystemEvents.SessionEnding += (_, _) => AgentCommands.StopForSessionEnd();
using var log = AgentLog.Open(newSession: false);
return await AgentCommands.RunAsync(["mount", .. args.Where(a => !a.Equals(AgentSupervisor.ChildFlag, StringComparison.OrdinalIgnoreCase))], log, log, timestamps: true, tray: true);
