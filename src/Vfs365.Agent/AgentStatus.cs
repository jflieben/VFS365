using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vfs365.Agent;

/// <summary>A file waiting to upload and why (the engine's last notice for it).</summary>
public sealed record WaitingFile(string Path, string Cause, DateTimeOffset Since);

/// <summary>
/// What the running agent reports about itself in %LOCALAPPDATA%\VFS365\status.json, for `vfs365 health` and support bundles.
/// State: starting, waiting (network or sign-in), not started (Problem says why), mounted or stopped. HeldChanges: changed files on
/// this PC that aren't queued for upload (still open, held under a temp name, or refused by SharePoint).
/// </summary>
public sealed record AgentStatus
{
    public const string Starting = "starting", Waiting = "waiting", NotStarted = "not started", Mounted = "mounted", Stopped = "stopped";

    public string Version { get; init; } = "";
    public int ProcessId { get; init; }
    public int SessionId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public string State { get; init; } = Starting;
    public string? Problem { get; init; }
    public string? DriveLetter { get; init; }
    public string? Unc { get; init; }
    public string? User { get; init; }
    public string? TenantId { get; init; }
    public string? SignInProblem { get; init; }
    public bool Online { get; init; } = true;
    public bool Throttled { get; init; }
    public int WaitingUploads { get; init; }
    public int HeldChanges { get; init; }
    public string? LastError { get; init; }
    public DateTimeOffset? LastErrorAt { get; init; }
    public IReadOnlyList<WaitingFile> WaitingFiles { get; init; } = [];

    [JsonIgnore]
    public bool IsMounted => State == Mounted;

    public static AgentStatus ForThisProcess(DateTimeOffset now)
    {
        using var process = Process.GetCurrentProcess();
        return new AgentStatus
        {
            Version = typeof(AgentStatus).Assembly.GetName().Version!.ToString(3),
            ProcessId = Environment.ProcessId,
            SessionId = process.SessionId,
            StartedAt = now,
            UpdatedAt = now,
        };
    }
}

/// <summary>
/// Writes the agent's status file: atomically (a temporary file moved into place), only when something changed or every
/// <see cref="Heartbeat"/>, so `vfs365 health` can tell a running agent from one that hangs or ended (<see cref="Stale"/>). Live, once
/// set, fills in what the mounted drive knows (counts, network, sign-in) every <see cref="TickInterval"/>. Never throws: a status that
/// can't be written is tried again at the next tick.
/// </summary>
public sealed class StatusFile : IDisposable
{
    public const string FileName = "status.json";
    public const int MaxWaitingFiles = 20;
    public static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(5), Stale = TimeSpan.FromMinutes(15), TickInterval = TimeSpan.FromSeconds(5);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly string path;
    readonly Func<DateTimeOffset> clock;
    readonly Lock gate = new();
    readonly Timer? timer;
    readonly Dictionary<string, WaitingFile> waiting = new(StringComparer.OrdinalIgnoreCase);
    AgentStatus current;
    string? written;
    DateTimeOffset writtenAt = DateTimeOffset.MinValue;
    bool closed;

    public static string DefaultPath { get; } = Path.Combine(DiscoveryStore.DataDirectory, FileName);

    /// <param name="ticking">False in tests: Tick is then called by hand.</param>
    public StatusFile(string path, AgentStatus initial, Func<DateTimeOffset>? clock = null, bool ticking = true)
    {
        (this.path, this.clock, current) = (path, clock ?? (() => DateTimeOffset.UtcNow), initial);
        Write(force: true);
        timer = ticking ? new Timer(_ => Tick(), null, TickInterval, TickInterval) : null;
    }

    /// <summary>Fills in the live fields at each tick (set once mounted).</summary>
    public Func<AgentStatus, AgentStatus>? Live { get; set; }

    public AgentStatus Current
    {
        get
        {
            lock (gate)
            {
                return current;
            }
        }
    }

    public void Update(Func<AgentStatus, AgentStatus> change)
    {
        lock (gate)
        {
            current = change(current);
        }
        Write(force: false);
    }

    /// <summary>The last error, redacted (it may hold a request's URL); written at the next tick, so a burst of errors is one write.</summary>
    public void Error(string message)
    {
        var text = Redactor.Redact(message.Split('\n')[0].Trim());
        lock (gate)
        {
            current = current with { LastError = text.Length <= 500 ? text : text[..500], LastErrorAt = clock() };
        }
    }

    /// <summary>Remembers "error: " lines (the engine's, the file system's and the agent's own) as the last error.</summary>
    public Action<string> Tap(Action<string> log) => line =>
    {
        log(line);
        if (line.StartsWith("error: ", StringComparison.Ordinal))
        {
            Error(line["error: ".Length..]);
        }
    };

    /// <summary>A file waiting to upload and why; <paramref name="cause"/> null when it uploaded.</summary>
    public void FileWaiting(string path, string? cause)
    {
        lock (gate)
        {
            if (cause is null)
            {
                waiting.Remove(path);
            }
            else if (waiting.Count < 1000 || waiting.ContainsKey(path))
            {
                waiting[path] = new WaitingFile(path, Redactor.Redact(cause.Split('\n')[0].Trim()), waiting.GetValueOrDefault(path)?.Since ?? clock());
            }
            current = current with { WaitingFiles = waiting.Values.OrderBy(w => w.Since).Take(MaxWaitingFiles).ToList() };
        }
    }

    /// <summary>Live runs under the status's lock, so it must not log (logging can update the status).</summary>
    public void Tick()
    {
        try
        {
            if (Live is { } live)
            {
                lock (gate)
                {
                    current = live(current);
                    if (current.WaitingUploads == 0 && waiting.Count > 0)
                    {
                        waiting.Clear(); // renamed or deleted meanwhile: no notice says they are done
                        current = current with { WaitingFiles = [] };
                    }
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
        Write(force: false);
    }

    /// <summary>The last status (stopped), then no more writes.</summary>
    public void Close(string state = AgentStatus.Stopped)
    {
        timer?.Dispose();
        Live = null;
        Update(s => s with { State = state, DriveLetter = null, WaitingUploads = 0 });
        lock (gate)
        {
            closed = true;
        }
    }

    public void Dispose() => timer?.Dispose();

    void Write(bool force)
    {
        lock (gate)
        {
            if (closed)
            {
                return;
            }
            var now = clock();
            var content = JsonSerializer.Serialize(current with { UpdatedAt = default }, Json);
            if (!force && content == written && now - writtenAt < Heartbeat)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(current with { UpdatedAt = now }, Json));
                File.Move(temporary, path, overwrite: true);
                (written, writtenAt) = (content, now);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    public static AgentStatus? Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentStatus>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static AgentStatus? Read(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
