using System.Diagnostics;

namespace Vfs365.Agent;

/// <summary>
/// vfs365-agent.exe as started at sign-in: runs the agent as a child process (--child) and starts it again when it crashes, so a fault
/// in the agent or in a component it loads doesn't leave the user without the drive until the next sign-in. A normal stop (unmount,
/// sign-out, uninstall, a restart from the tray) or a problem the agent reported itself ends the supervisor too. At most MaxRestarts
/// within RestartWindow; crash details from the Windows event log go to agent.log.
/// </summary>
public static class AgentSupervisor
{
    public const string ChildFlag = "--child", RestartedFlag = "--restarted";
    const int MaxRestarts = 3;
    static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(15), RestartDelay = TimeSpan.FromSeconds(3);

    public static int Run(string[] args, Func<bool> sessionEnding)
    {
        using (AgentLog.Open(newSession: !args.Contains("--after", StringComparer.OrdinalIgnoreCase)))
        {
            // Starts this sign-in's log; the child appends to it
        }

        var restarts = new Queue<DateTime>();
        var childArgs = args.ToList();
        while (true)
        {
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in childArgs.Prepend(ChildFlag))
            {
                start.ArgumentList.Add(argument);
            }
            int code;
            int childId;
            using (var child = Process.Start(start)!)
            {
                childId = child.Id;
                child.WaitForExit();
                code = child.ExitCode;
            }
            if (!Crashed(code) || sessionEnding())
            {
                return code;
            }

            var now = DateTime.Now;
            while (restarts.TryPeek(out var first) && now - first > RestartWindow)
            {
                restarts.Dequeue();
            }
            // Windows records the crash only once the process is gone: the details follow in the log while the agent starts again
            var details = Task.Run(() => WriteCrashDetails(childId));
            if (restarts.Count >= MaxRestarts)
            {
                Write([$"Agent:      stopped unexpectedly (exit code 0x{code:X8}); not started again: it stopped {MaxRestarts + 1} times within " +
                    $"{RestartWindow.TotalMinutes:0} minutes. It starts at the next sign-in."]);
                details.Wait(TimeSpan.FromSeconds(30));
                return code;
            }
            restarts.Enqueue(now);
            Write([$"Agent:      stopped unexpectedly (exit code 0x{code:X8}); starting again"]);
            Thread.Sleep(RestartDelay);
            if (sessionEnding())
            {
                return code;
            }

            // The first start's --after (a restart from the tray) is done; the new child reports the crash
            childArgs = WithoutOption(WithoutOption(args.ToList(), "--after"), RestartedFlag);
            childArgs.AddRange([RestartedFlag, $"0x{code:X8}"]);
        }
    }

    /// <summary>
    /// A crash: an NTSTATUS error (access violation, fail-fast, a CLR exception) rather than a normal or reported exit (0, 1, 2), a
    /// kill (-1, from Task Manager or the uninstaller) or Ctrl+C.
    /// </summary>
    public static bool Crashed(int code) => ((uint)code & 0xC0000000) == 0xC0000000 && code != -1 && (uint)code != 0xC000013A;

    static List<string> WithoutOption(List<string> args, string name)
    {
        if (args.FindIndex(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)) is var at and >= 0)
        {
            args.RemoveRange(at, Math.Min(2, args.Count - at));
        }
        return args;
    }

    static void Write(IEnumerable<string> lines)
    {
        try
        {
            using var log = AgentLog.Open(newSession: false);
            foreach (var line in lines)
            {
                log.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Looks for the crash's events for up to 20 seconds and logs them, or that there were none.</summary>
    static void WriteCrashDetails(int processId)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            Thread.Sleep(TimeSpan.FromSeconds(2));
            if (CrashDetails(processId).ToList() is { Count: > 0 } found)
            {
                Write([$"Agent:      what Windows recorded about the stop of process {processId}:", .. found.Select(line => $"            {line}")]);
                return;
            }
        }
        Write([$"Agent:      Windows recorded nothing about the stop of process {processId} (see Event Viewer, Application log, sources .NET Runtime and Application Error)"]);
    }

    /// <summary>What Windows recorded about the crash (.NET Runtime 1026 or Application Error 1000): the exception and the top of the stack.</summary>
    static IEnumerable<string> CrashDetails(int processId)
    {
        try
        {
            var query = new ProcessStartInfo("wevtutil.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (var argument in new[] { "qe", "Application", "/q:*[System[(EventID=1026 or EventID=1000) and TimeCreated[timediff(@SystemTime) <= 120000]]]", "/c:6", "/rd:true", "/f:text" })
            {
                query.ArgumentList.Add(argument);
            }
            using var process = Process.Start(query)!;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000) || !output.Wait(1000))
            {
                return [];
            }
            // Events are separated by "Event[n]" headers, newest first. Application Error names the process ID, the faulting module and the
            // code; .NET Runtime (no process ID) the exception and the managed stack.
            var events = output.Result.Split("Event[", StringSplitOptions.RemoveEmptyEntries)
                .Where(e => e.Contains("vfs365-agent", StringComparison.OrdinalIgnoreCase)).ToList();
            var fault = events.FirstOrDefault(e => e.Contains($"Faulting process id: 0x{processId:x}", StringComparison.OrdinalIgnoreCase));
            if (fault is null)
            {
                return [];
            }
            static IEnumerable<string> From(string text, string first, int count) =>
                text.Split('\n').Select(l => l.Trim()).SkipWhile(l => !l.StartsWith(first, StringComparison.Ordinal)).Where(l => l.Length > 0).Take(count);
            var managed = events.FirstOrDefault(e => e.Contains("Exception Info", StringComparison.Ordinal));
            return [.. From(fault, "Faulting module name", 3), .. managed is null ? [] : From(managed, "Exception Info", 10)];
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }
}
