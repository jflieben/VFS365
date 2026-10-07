using Microsoft.Win32;
using Vfs365.Agent;

// Output goes to %LOCALAPPDATA%\VFS365\agent.log. Logoff, shutdown and the installer (Restart Manager) end the session cleanly:
// the navigation pane entry goes at once, then the mount flushes and stops.
SystemEvents.SessionEnding += (_, _) => AgentCommands.StopForSessionEnd();
using var log = AgentLog.Open();
return await AgentCommands.RunAsync(["mount", .. args], log, log, timestamps: true, tray: true);
