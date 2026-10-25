using System.Diagnostics;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Vfs365.Agent;

/// <summary>
/// vfs365-agent.exe as started at sign-in: runs the agent as a child process (--child) and starts it again when it crashes, so a fault
/// in the agent or in a component it loads doesn't leave the user without the drive until the next sign-in. A normal stop (unmount,
/// sign-out, uninstall, a restart from the tray) or a problem the agent reported itself ends the supervisor too. At most MaxRestarts
/// within RestartWindow; crash details from the Windows event log go to agent.log, and giving up goes to monitoring as a crash row and
/// to the tray (unless policy hides it), whose Retry starts the agent again.
/// </summary>
public static class AgentSupervisor
{
    public const string ChildFlag = "--child", RestartedFlag = "--restarted";
    const int MaxRestarts = 3;
    static readonly TimeSpan RestartWindow = TimeSpan.FromMinutes(15), RestartDelay = TimeSpan.FromSeconds(3);

    public static int Run(string[] args, Func<bool> sessionEnding)
    {
        // Starts this sign-in's log, which the child appends to; not while an agent runs in this session (a second start, a restart).
        // A profile that can't be written yet is the child's to report (its log falls back to %TEMP%).
        try
        {
            using (AgentLog.Open(newSession: !args.Contains("--after", StringComparer.OrdinalIgnoreCase) && !AgentControl.IsMounted()))
            {
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
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
            // The first start's --after (a restart from the tray) is done
            childArgs = WithoutOption(WithoutOption(args.ToList(), "--after"), RestartedFlag);
            if (restarts.Count >= MaxRestarts)
            {
                var gaveUp = $"The agent stopped unexpectedly (exit code 0x{code:X8}) {MaxRestarts + 1} times within {RestartWindow.TotalMinutes:0} minutes " +
                    "and was not started again; it starts at the next sign-in, or at Retry in the tray";
                Write([$"Agent:      {gaveUp}."]);
                AgentEvents.Write(AgentEvent.NotRestarted, gaveUp);
                var settings = Settings(args);
                ReportGivingUp(settings, gaveUp);
                details.Wait(TimeSpan.FromSeconds(30));
                if (!WaitForRetry(settings, sessionEnding))
                {
                    return code;
                }
                Write(["Agent:      starting again (Retry in the tray)"]);
                restarts.Clear();
                continue;
            }
            restarts.Enqueue(now);
            Write([$"Agent:      stopped unexpectedly (exit code 0x{code:X8}); starting again"]);
            AgentEvents.Write(AgentEvent.Crashed, $"The agent stopped unexpectedly (exit code 0x{code:X8}) and is started again; agent.log has what Windows recorded");
            Thread.Sleep(RestartDelay);
            if (sessionEnding())
            {
                return code;
            }
            childArgs.AddRange([RestartedFlag, $"0x{code:X8}"]); // the new child reports the crash
        }
    }

    /// <summary>
    /// A crash: any exit but the agent's own (0 stopped, 1 a problem it reported, 2 usage), a kill (-1: Task Manager, the uninstaller),
    /// Ctrl+C (STATUS_CONTROL_C_EXIT) and an end at logoff (DBG_TERMINATE_PROCESS). So NTSTATUS errors, CLR exits (an unhandled
    /// exception 0xE0434352, fail-fast 0x80131623) and anything else unexpected.
    /// </summary>
    public static bool Crashed(int code) => code is not (0 or 1 or 2 or -1) && (uint)code is not (0xC000013A or 0x40010004);

    /// <summary>Settings for the supervisor's own reports and tray; null when they can't be read.</summary>
    static AgentSettings? Settings(string[] args)
    {
        try
        {
            var config = Array.IndexOf(args, "--config") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;
            return AgentSettings.Load(config);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// After giving up: a tray icon that says so, with Retry and Show log, until Retry (true) or the end of the session. False at once
    /// when policy hides the tray icon.
    /// </summary>
    static bool WaitForRetry(AgentSettings? settings, Func<bool> sessionEnding)
    {
        if (settings is { TrayIcon: false } || sessionEnding())
        {
            return false;
        }
        using var retry = new ManualResetEventSlim(false);
        var ended = false;
        // What 'vfs365 health' reads meanwhile: no drive, and why
        using var status = new StatusFile(StatusFile.DefaultPath, (StatusFile.Read(StatusFile.DefaultPath) ?? AgentStatus.ForThisProcess(DateTimeOffset.UtcNow)) with
        {
            ProcessId = Environment.ProcessId,
            State = AgentStatus.NotStarted,
            Problem = "stopped unexpectedly several times; Retry in the tray starts it again",
            DriveLetter = null,
        });
        Tray? shown = null;
        using var tray = shown = new Tray(settings?.Label ?? AgentSettings.DefaultLabel, new TrayActions(settings?.HelpUrl ?? AgentSettings.DefaultHelpUrl)
        {
            Retry = retry.Set,
            SessionEnded = () => ended = true,
            CollectDiagnostics = () => SupportBundle.CollectFromTray(null, line => Write([$"Agent:      {line}"]), (title, text) => shown?.Notify(title, text)),
        }, line => Write([$"Agent:      {line}"]));
        tray.SetStatus(UiText.Get("Status.Stopped"), TrayState.Error);
        tray.Notify(UiText.Get("Stopped.Title"), UiText.Get("Stopped.Text"));
        while (!retry.Wait(TimeSpan.FromSeconds(1)))
        {
            if (ended || sessionEnding())
            {
                return false;
            }
        }
        return true;
    }

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
            foreach (var argument in new[] { "qe", "Application", "/q:*[System[(EventID=1026 or EventID=1000) and TimeCreated[timediff(@SystemTime) <= 120000]]]", "/c:6", "/rd:true", "/f:xml" })
            {
                query.ArgumentList.Add(argument);
            }
            using var process = Process.Start(query)!;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000) || !output.Wait(1000))
            {
                return [];
            }
            return ParseCrashEvents(output.Result, processId);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// The crash of <paramref name="processId"/> from wevtutil's XML (events side by side, newest first), whatever the Windows language:
    /// Application Error (1000) by its EventData names, or by position where a Windows version leaves them unnamed, gives the faulting
    /// module and the exception code; .NET Runtime (1026, no process ID) holds the exception and the managed stack as one text.
    /// </summary>
    public static IReadOnlyList<string> ParseCrashEvents(string xml, int processId)
    {
        XElement root;
        try
        {
            root = XElement.Parse($"<Events>{xml}</Events>");
        }
        catch (XmlException)
        {
            return [];
        }
        var events = root.Elements().Where(e => e.Name.LocalName == "Event").ToList();
        static string? EventId(XElement e) => e.Descendants().FirstOrDefault(d => d.Name.LocalName == "EventID")?.Value.Trim();
        static List<XElement> Data(XElement e) => e.Descendants().Where(d => d.Name.LocalName == "Data").ToList();
        static string? Field(List<XElement> data, string name, int position) => data.Any(d => d.Attribute("Name") is not null)
            ? data.FirstOrDefault(d => (string?)d.Attribute("Name") == name)?.Value
            : data.ElementAtOrDefault(position)?.Value;

        var fault = events.Where(e => EventId(e) == "1000").Select(Data).FirstOrDefault(d =>
            (Field(d, "AppName", 0) ?? "").Contains("vfs365-agent", StringComparison.OrdinalIgnoreCase) && SameProcess(Field(d, "ProcessId", 8), processId));
        if (fault is null)
        {
            return [];
        }
        var lines = new List<string>
        {
            $"Faulting module: {Field(fault, "ModuleName", 3)} {Field(fault, "ModuleVersion", 4)}, exception code {Hex(Field(fault, "ExceptionCode", 6))}, " +
            $"offset {Hex(Field(fault, "FaultingOffset", 7))}",
        };
        var managed = events.Where(e => EventId(e) == "1026").Select(e => string.Join("\n", Data(e).Select(d => d.Value)))
            .FirstOrDefault(text => text.Contains("vfs365-agent", StringComparison.OrdinalIgnoreCase));
        if (managed is not null)
        {
            var text = managed.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            var from = text.FindIndex(l => l.Contains("Exception Info", StringComparison.Ordinal));
            lines.AddRange(text.Skip(from >= 0 ? from : 2).Take(10));
        }
        return lines;
    }

    /// <summary>Hex as Windows writes it in these events: "0x1f34", "1f34", or "0x0x1f34" on some versions.</summary>
    static long? ParseHex(string? value)
    {
        var text = value?.Trim() ?? "";
        while (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        return long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var number) ? number : null;
    }

    static string? Hex(string? value) => ParseHex(value) is { } number ? $"0x{number:x8}" : value;

    static bool SameProcess(string? value, int processId) => ParseHex(value) == processId;

    /// <summary>A crash row from the supervisor itself, as no agent runs to report it, with the last known account. Best effort, 10 s at most.</summary>
    static void ReportGivingUp(AgentSettings? settings, string message)
    {
        try
        {
            if (settings is null || MonitoringTarget.Parse(settings.MonitoringUrl, out _) is not { } target)
            {
                return;
            }
            var tenant = TenantResolver.Resolve(settings, DeviceJoin.Read()).Tenant;
            var user = UsageStatistics.Load().LastUser();
            var monitoring = new Monitoring(target, DeviceInfo.Current(), line => Write([line]))
            {
                TenantId = () => tenant,
                User = () => user,
                Detail = settings.MonitoringDetail,
            };
            monitoring.Error("crash", message);
            monitoring.FlushAsync(TimeSpan.FromSeconds(10)).Wait();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }
}
