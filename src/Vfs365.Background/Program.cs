using Microsoft.Win32;
using Vfs365.Agent;

// Started at sign-in, this is the supervisor: it runs the agent as a child process and starts it again after a crash. The child writes
// to %LOCALAPPDATA%\VFS365\agent.log, a new file per sign-in (a restart from the tray, with --after, appends). The session ends cleanly
// on logoff, shutdown and the installer (Restart Manager): only once Windows really ends it (SessionEnded, not a shutdown that is still
// asked about and can be cancelled), redirected folders go back, the drive letters for folders and the navigation pane entry go, then
// the mount uploads pending changes and stops.
if (args is ["verb", ..])
{
    return VerbPipe.RunClient(args); // Explorer's VFS365 menu: hands the request to the running agent and ends
}
if (!args.Contains(AgentSupervisor.ChildFlag, StringComparer.OrdinalIgnoreCase))
{
    var ending = false;
    SystemEvents.SessionEnded += (_, _) => ending = true; // the child is ended at logoff too: no restart then
    return AgentSupervisor.Run(args, () => ending);
}
SystemEvents.SessionEnded += (_, _) => AgentCommands.StopForSessionEnd();
SystemEvents.PowerModeChanged += (_, e) =>
{
    if (e.Mode is PowerModes.Suspend or PowerModes.Resume)
    {
        AgentCommands.PowerChanged(resumed: e.Mode == PowerModes.Resume);
    }
};
using var log = AgentLog.OpenGuarded(newSession: false); // a profile that isn't writable yet at logon never ends the agent
return await AgentCommands.RunAsync(["mount", .. args.Where(a => !a.Equals(AgentSupervisor.ChildFlag, StringComparison.OrdinalIgnoreCase))], log, log, timestamps: true, tray: true);
