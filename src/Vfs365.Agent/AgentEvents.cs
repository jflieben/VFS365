using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace Vfs365.Agent;

public enum EventType : ushort { Error = 1, Warning = 2, Information = 4 }

/// <summary>
/// Events in the Application log under source VFS365, with fixed IDs (docs/DEPLOYMENT.md lists them). The MSI registers the source
/// with EventCreate.exe as its message file, whose message table has "%1" for every ID from 1 to 1000, so the text is the message.
/// </summary>
public enum AgentEvent
{
    Started = 100,
    Stopped = 101,
    Restarting = 102,
    NotStarted = 103,
    Crashed = 110,
    NotRestarted = 111,
    SignInNeeded = 200,
    UncPathTaken = 300,
    NameResolves = 301,
    ConflictCopy = 400,
    UploadWaiting = 401,
    DeleteRefused = 402,
    NotSaved = 403,
    Recovered = 404,
    SettingWarning = 500,
    SettingsChanged = 501,
    DriveLetterProblem = 502,
    RedirectionProblem = 503,
    ProviderOrder = 504,
    Throttled = 600,
    DiscoveryFailed = 700,
}

public interface IEventSink
{
    void Write(int id, EventType type, string message);
}

/// <summary>
/// Writes events to a sink, redacted and rate-limited per ID: the same message at most once per <see cref="Window"/>, and at most
/// <see cref="MaxPerWindow"/> of one ID per window. The next event of that ID that is written says how many were left out.
/// </summary>
public sealed class EventReporter(IEventSink? sink, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);
    public const int MaxPerWindow = 10, MaxLength = 8000;

    sealed class Recent
    {
        public Queue<DateTimeOffset> Written { get; } = new();
        public Dictionary<string, DateTimeOffset> Messages { get; } = new(StringComparer.Ordinal);
        public int LeftOut { get; set; }
        public DateTimeOffset LeftOutSince { get; set; }
    }

    readonly Func<DateTimeOffset> clock = clock ?? (() => DateTimeOffset.Now);
    readonly Dictionary<AgentEvent, Recent> recent = [];

    public static EventType TypeOf(AgentEvent kind) => kind switch
    {
        AgentEvent.NotStarted or AgentEvent.Crashed or AgentEvent.NotRestarted or AgentEvent.UncPathTaken or AgentEvent.NotSaved => EventType.Error,
        AgentEvent.Started or AgentEvent.Stopped or AgentEvent.Restarting or AgentEvent.Recovered or AgentEvent.SettingsChanged => EventType.Information,
        _ => EventType.Warning,
    };

    /// <summary>True when the event went to the sink; false without a sink, or when the rate limit left it out.</summary>
    public bool Write(AgentEvent kind, string message)
    {
        if (sink is null)
        {
            return false;
        }
        lock (recent)
        {
            var now = clock();
            if (!recent.TryGetValue(kind, out var state))
            {
                recent[kind] = state = new Recent();
            }
            while (state.Written.TryPeek(out var first) && now - first >= Window)
            {
                state.Written.Dequeue();
            }
            foreach (var old in state.Messages.Where(m => now - m.Value >= Window).Select(m => m.Key).ToList())
            {
                state.Messages.Remove(old);
            }
            if (state.Messages.ContainsKey(message) || state.Written.Count >= MaxPerWindow)
            {
                if (state.LeftOut++ == 0)
                {
                    state.LeftOutSince = now;
                }
                return false;
            }
            var text = state.LeftOut == 0 ? message : $"{message} ({state.LeftOut} more event(s) of this kind since {state.LeftOutSince:yyyy-MM-dd HH:mm} were not written)";
            state.LeftOut = 0;
            state.Written.Enqueue(now);
            state.Messages[message] = now;
            text = Redactor.Redact(text);
            sink.Write((int)kind, TypeOf(kind), text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…");
            return true;
        }
    }
}

/// <summary>The process's events; written only where the MSI registered the source (not in development runs). Never throws.</summary>
public static class AgentEvents
{
    static readonly Lazy<EventReporter> Reporter = new(() => new EventReporter(WindowsEventSink.Registered() ? new WindowsEventSink() : null));

    public static void Write(AgentEvent kind, string message)
    {
        try
        {
            Reporter.Value.Write(kind, message);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }
}

/// <summary>The Application log through advapi32, with the user's SID, so multi-session hosts show whose agent wrote it.</summary>
public sealed class WindowsEventSink : IEventSink
{
    public const string Source = "VFS365";
    public const string RegistryKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + Source;

    readonly IntPtr handle = RegisterEventSourceW(null, Source);
    readonly byte[]? user = UserSid();

    /// <summary>The MSI's registration of the source; without it Event Viewer couldn't show the text.</summary>
    public static bool Registered()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(RegistryKey);
            return key?.GetValue("EventMessageFile") is string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public void Write(int id, EventType type, string message)
    {
        if (handle != IntPtr.Zero)
        {
            ReportEventW(handle, (ushort)type, 0, (uint)id, user, 1, 0, [message], IntPtr.Zero);
        }
    }

    static byte[]? UserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (identity.User is not { } sid)
        {
            return null;
        }
        var bytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(bytes, 0);
        return bytes;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr RegisterEventSourceW(string? server, string source);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool ReportEventW(IntPtr log, ushort type, ushort category, uint eventId, byte[]? userSid, ushort count, uint dataSize,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] strings, IntPtr data);
}
