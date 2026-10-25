using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>Where reports go: an Azure table service URL with a SAS (policy MonitoringUrl).</summary>
public sealed record MonitoringTarget(string BaseUrl, string Query, DateTimeOffset? Expires)
{
    /// <summary>The storage account (host and path), without the SAS: safe to log.</summary>
    public string Account => BaseUrl[(BaseUrl.IndexOf("://", StringComparison.Ordinal) + 3)..];

    public Uri TableUri(string table) => new($"{BaseUrl}/{table}{Query}");

    /// <summary>
    /// Null when <paramref name="url"/> is empty (no reporting) or not usable (<paramref name="problem"/> says why). Every user on the PC
    /// can read the URL from the policy, so a SAS that can read, list, change or delete rows, or reaches other services, is refused. Meant
    /// is an account SAS for the Table service, objects and Add only (ss=t, srt=o, sp=a), or a table SAS with Add only (tn, sp=a).
    /// </summary>
    public static MonitoringTarget? Parse(string? url, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            || !uri.Query.Contains("sig=", StringComparison.OrdinalIgnoreCase))
        {
            problem = "MonitoringUrl is not an https table service URL with a SAS (sv=...&sig=...)";
            return null;
        }
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = part.IndexOf('=');
            fields[at < 0 ? part : part[..at]] = at < 0 ? "" : Uri.UnescapeDataString(part[(at + 1)..]);
        }
        if (RightsProblem(fields) is { } tooMuch)
        {
            problem = tooMuch;
            return null;
        }
        DateTimeOffset? expires = fields.TryGetValue("se", out var se)
            && DateTimeOffset.TryParse(se, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until) ? until : null;
        return new($"{uri.GetLeftPart(UriPartial.Authority)}{uri.AbsolutePath.TrimEnd('/')}", uri.Query, expires);
    }

    /// <summary>Null when the SAS fields (sp, ss, srt, sr, tn) allow adding table rows and nothing else; else what is wrong and the fix.</summary>
    static string? RightsProblem(IReadOnlyDictionary<string, string> fields)
    {
        var wrong = new List<string>();
        if (fields.TryGetValue("sr", out var resource))
        {
            wrong.Add($"it is for Blob or File storage (sr={resource})");
        }
        if (!fields.TryGetValue("sp", out var permissions))
        {
            wrong.Add("it doesn't list its permissions (no sp; a stored access policy can't be checked)");
        }
        else if (!permissions.Contains('a') || permissions.Any(p => p is not ('a' or 'c')))
        {
            // Create (c) only adds tables; read, list, update and delete expose or change other devices' rows
            wrong.Add($"permissions sp={permissions}, not Add only (a)");
        }
        // An account SAS; a table SAS (tn) has neither ss nor srt
        if (fields.ContainsKey("ss") || fields.ContainsKey("srt") || !fields.ContainsKey("tn"))
        {
            var services = fields.GetValueOrDefault("ss");
            var types = fields.GetValueOrDefault("srt");
            if (services != "t")
            {
                wrong.Add(services is null ? "no allowed services (ss)" : $"services ss={services}, not Table only (t)");
            }
            if (types is null || !types.Contains('o'))
            {
                wrong.Add(types is null ? "no allowed resource types (srt)" : $"resource types srt={types}, without Object (o)");
            }
        }
        return wrong.Count == 0 ? null
            : $"MonitoringUrl's SAS can do more than add table rows, or that can't be checked: {string.Join("; ", wrong)}. Every user on this PC can " +
              "read it from the policy, so VFS365 doesn't use it. Make a SAS with allowed services Table, resource types Object and permissions Add " +
              $"only (ss=t, srt=o, sp=a); see {Monitoring.Instructions}";
    }
}

/// <summary>The device as reports name it: computer name, Windows' MachineGuid, and the Entra device ID when joined or registered.</summary>
public sealed record DeviceInfo(string Name, string Id, string? EntraDeviceId)
{
    public static DeviceInfo Current()
    {
        string? machineGuid = null;
        try
        {
            using var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            machineGuid = key?.GetValue("MachineGuid") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
        }
        return new(Environment.MachineName, machineGuid ?? Environment.MachineName, DeviceJoin.Read()?.DeviceId);
    }
}

/// <summary>
/// Best-effort reports to the customer's own Azure table storage: errors at once (at most 20 an hour, a repeat once an hour), usage
/// per day (sent the day after), installs, updates and uninstalls. Fire and forget: nothing waits for a report, error rows are not
/// retried (a day's statistics stay in statistics.json until they arrive), and the first failure is logged once. Retarget follows a
/// policy change while the agent runs.
/// </summary>
public sealed class Monitoring(MonitoringTarget target, DeviceInfo device, Action<string> log)
{
    volatile MonitoringTarget? current = target;

    public const string ErrorsTable = "errors";
    public const string StatisticsTable = "statistics";
    public const string DevicesTable = "devices";
    public const string Instructions = "https://github.com/jflieben/VFS365/blob/main/docs/DEPLOYMENT.md#monitoring";
    const int MaxErrorsPerHour = 20;

    static readonly HttpClient http = new(Vfs365.Graph.M365Client.CreateHandler()) { Timeout = TimeSpan.FromSeconds(15) };

    readonly Lock gate = new();
    readonly List<Task> pending = [];
    readonly Dictionary<string, DateTimeOffset> reported = new(StringComparer.Ordinal);
    DateTimeOffset hourStarted = DateTimeOffset.MinValue;
    int reportedThisHour;
    int errors;
    int failed;

    public static string Version => typeof(Monitoring).Assembly.GetName().Version!.ToString(3);

    public static string OffHint =>
        $"Monitoring: off. Errors and daily usage can go to your own Azure table storage through the MonitoringUrl policy; instructions at {Instructions}";

    /// <summary>Where reports go; null after Retarget turned reporting off.</summary>
    public MonitoringTarget? Target => current;
    public DeviceInfo Device => device;

    /// <summary>The signed-in account (UPN) and its tenant, once known.</summary>
    public Func<string?> User { get; set; } = () => null;
    public Func<string?> TenantId { get; set; } = () => null;

    /// <summary>Errors seen, reported or not, for the day's statistics.</summary>
    public int Errors => Volatile.Read(ref errors);

    public string Started => current is not { } to ? "Monitoring: off"
        : $"Monitoring: errors and daily statistics to {to.Account}" + (to.Expires is { } expires ? $" (SAS valid until {expires.ToLocalTime():yyyy-MM-dd HH:mm})" : "");

    /// <summary>Full, or NoPaths: file and folder paths become a short hash and URLs keep only their host (Scrub).</summary>
    public MonitoringDetail Detail { get; set; } = MonitoringDetail.Full;

    /// <summary>A changed MonitoringUrl or MonitoringDetail, applied at once (a renewed SAS needs no restart); null stops reporting.</summary>
    public void Retarget(MonitoringTarget? to, MonitoringDetail detail)
    {
        current = to;
        Detail = detail;
        Interlocked.Exchange(ref failed, 0); // the first failure with the new SAS is logged again
    }

    /// <summary>An error row. <paramref name="paths"/> are the volume paths and file names in <paramref name="message"/>, hashed with NoPaths.</summary>
    public void Error(string source, string message, params string?[] paths)
    {
        if (Detail == MonitoringDetail.NoPaths)
        {
            message = Scrub(message, paths);
        }
        Interlocked.Increment(ref errors);
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (now - hourStarted >= TimeSpan.FromHours(1))
            {
                hourStarted = now;
                reportedThisHour = 0;
                foreach (var old in reported.Where(r => now - r.Value >= TimeSpan.FromHours(1)).Select(r => r.Key).ToList())
                {
                    reported.Remove(old);
                }
            }
            var key = $"{source}|{message}";
            if (reportedThisHour >= MaxErrorsPerHour || reported.ContainsKey(key))
            {
                return;
            }
            reportedThisHour++;
            reported[key] = now;
        }
        Track(SendAsync(ErrorsTable, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), NewestFirst(now),
            [.. Common(), ("Source", source), ("Message", message.Length > 8000 ? message[..8000] : message), ("OccurredAt", now)]));
    }

    /// <summary>
    /// A throttling episode as a row in the errors table (source "throttled", time = its start) with its numbers as columns. Not
    /// counted as an error; at most <see cref="MaxThrottlingPerHour"/> an hour.
    /// </summary>
    public void Throttling(ThrottleEpisodes.Episode episode)
    {
        var now = DateTimeOffset.UtcNow;
        lock (gate)
        {
            if (now - throttlingHour >= TimeSpan.FromHours(1))
            {
                throttlingHour = now;
                throttlingThisHour = 0;
            }
            if (++throttlingThisHour > MaxThrottlingPerHour)
            {
                return;
            }
        }
        Track(SendAsync(ErrorsTable, episode.From.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), NewestFirst(episode.From),
        [
            .. Common(), ("Source", "throttled"), ("Message", episode.Describe()), ("OccurredAt", episode.From), ("ThrottledUntil", episode.Until),
            ("Responses", episode.Responses), ("HeldSeconds", (int)Math.Ceiling(episode.Held.TotalSeconds)), ("Status429", episode.Status429),
            ("Status503", episode.Status503), ("GraphResponses", episode.Graph), ("SharePointResponses", episode.SharePoint),
            ("BackgroundResponses", episode.Background), ("LongestWaitSeconds", (int)Math.Ceiling(episode.LongestPause.TotalSeconds)),
        ]));
    }

    const int MaxThrottlingPerHour = 12;
    DateTimeOffset throttlingHour = DateTimeOffset.MinValue;
    int throttlingThisHour;

    /// <summary>
    /// Message text without names: the given paths and any other volume path (a word starting with a single backslash, up to the next
    /// colon or the end, as in file system log lines) become "[path 1a2b3c4d]", the first 8 hex digits of the SHA-256 of the lower-cased
    /// path, so repeats still group. URLs keep only their host; UNC paths (\\VFS365\user) stay.
    /// </summary>
    public static string Scrub(string message, IEnumerable<string?> paths)
    {
        static string Hash(string path) =>
            $"[path {Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant())))[..8]}]";
        foreach (var path in paths.Where(p => !string.IsNullOrEmpty(p)).OrderByDescending(p => p!.Length))
        {
            message = message.Replace(path!, Hash(path!), StringComparison.OrdinalIgnoreCase);
        }
        message = Regex.Replace(message, @"(https?://[^/\s]+)/\S*?(?=:?(\s|$))", "$1/...");
        return Regex.Replace(message, @"(?<=^|\s)\\(?!\\)[^\r\n:*?""<>|]+", m => Hash(m.Value.TrimEnd()));
    }

    /// <summary>
    /// One row per day, device and Windows user. True when it was added (or was there already). <paramref name="on"/>: the device the
    /// day's usage happened on (statistics.json roams with the profile on pooled hosts), else this one. <paramref name="salt"/>, random
    /// per day and kept with it, ends the row key: a re-send of the same day still conflicts (409, so it counts once), while nobody can
    /// add the row first and a day created again (after a time-zone change) gets a row of its own.
    /// </summary>
    public Task<bool> StatisticsAsync(string day, IEnumerable<(string Name, object? Value)> values, string? user = null, string? version = null,
        DeviceInfo? on = null, string? salt = null)
    {
        on ??= device;
        var rowKey = $"{on.Id}_{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}{(salt is null ? "" : $"_{salt}")}";
        var sending = SendAsync(StatisticsTable, day, rowKey, [.. Common(user, version, on), .. values]);
        Track(sending);
        return sending;
    }

    /// <summary>An install, update or uninstall; <paramref name="users"/> are the users signed in at the time. True when it was added.</summary>
    public Task<bool> DeviceAsync(string action, string? users)
    {
        var now = DateTimeOffset.UtcNow;
        return SendAsync(DevicesTable, device.Id, NewestFirst(now),
            [("DeviceName", device.Name), ("DeviceId", device.Id), ("EntraDeviceId", device.EntraDeviceId), ("Users", users), ("Version", Version), ("Action", action), ("OccurredAt", now)]);
    }

    /// <summary>Gives reports still on their way a moment before the process ends.</summary>
    public Task FlushAsync(TimeSpan timeout)
    {
        Task[] waiting;
        lock (gate)
        {
            waiting = [.. pending];
        }
        return Task.WhenAny(Task.WhenAll(waiting), Task.Delay(timeout));
    }

    /// <summary>What every row carries; a statistics row of an earlier day brings that day's user, version and device.</summary>
    IEnumerable<(string, object?)> Common(string? user = null, string? version = null, DeviceInfo? on = null) =>
    [
        ("DeviceName", (on ?? device).Name), ("DeviceId", (on ?? device).Id), ("EntraDeviceId", (on ?? device).EntraDeviceId), ("User", user ?? User()),
        ("WindowsUser", $@"{Environment.UserDomainName}\{Environment.UserName}"), ("TenantId", TenantId()), ("Version", version ?? Version),
    ];

    void Track(Task task)
    {
        lock (gate)
        {
            pending.RemoveAll(t => t.IsCompleted);
            pending.Add(task);
        }
    }

    /// <summary>Row keys that sort the newest first within a partition.</summary>
    static string NewestFirst(DateTimeOffset at) => $"{DateTimeOffset.MaxValue.UtcTicks - at.UtcTicks:D19}_{Guid.NewGuid():N}";

    /// <summary>True when the row was added (or was there already).</summary>
    async Task<bool> SendAsync(string table, string partitionKey, string rowKey, IEnumerable<(string Name, object? Value)> properties)
    {
        if (current is not { } to)
        {
            return false;
        }
        if (to.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            FailOnce(to, $"the SAS expired on {expires.ToLocalTime():yyyy-MM-dd HH:mm}");
            return false;
        }
        try
        {
            var content = new StringContent(EntityJson(partitionKey, rowKey, properties), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, to.TableUri(table)) { Content = content };
            request.Headers.TryAddWithoutValidation("Accept", "application/json;odata=nometadata");
            request.Headers.TryAddWithoutValidation("Prefer", "return-no-content");
            request.Headers.TryAddWithoutValidation("x-ms-version", "2019-02-02");
            request.Headers.TryAddWithoutValidation("DataServiceVersion", "3.0;NetFx");
            request.Headers.TryAddWithoutValidation("MaxDataServiceVersion", "3.0;NetFx");
            request.Headers.TryAddWithoutValidation("User-Agent", $"VFS365/{Version}");
            using var response = await http.SendAsync(request).ConfigureAwait(false);
            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
            {
                return true; // a conflict: that day's statistics were sent already
            }
            FailOnce(to, $"{(int)response.StatusCode} {Explain(await ErrorCodeAsync(response).ConfigureAwait(false), table)}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            FailOnce(to, e.Message);
        }
        return false;
    }

    void FailOnce(MonitoringTarget to, string detail)
    {
        if (Interlocked.Exchange(ref failed, 1) == 0)
        {
            log($"error: monitoring to {to.Account} failed: {detail}. Error rows are not retried; a day's statistics are, for up to a week; see {Instructions}");
        }
    }

    static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ms-error-code", out var codes) && codes.FirstOrDefault() is { Length: > 0 } header)
        {
            return header;
        }
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("odata.error", out var error) && error.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string Explain(string? code, string table) => code switch
    {
        "AuthenticationFailed" => "AuthenticationFailed: the SAS signature is not valid or it expired",
        "AuthorizationPermissionMismatch" => "AuthorizationPermissionMismatch: the SAS needs the Add permission (sp=a)",
        "AuthorizationResourceTypeMismatch" => "AuthorizationResourceTypeMismatch: the SAS needs resource type Object (srt=o)",
        "AuthorizationServiceMismatch" => "AuthorizationServiceMismatch: the SAS must be for the Table service (ss=t)",
        "TableNotFound" => $"TableNotFound: create the tables {ErrorsTable}, {StatisticsTable} and {DevicesTable} (missing: {table})",
        null => "no error code",
        _ => code,
    };

    /// <summary>A table entity in JSON: 64-bit numbers and times carry their OData type; empty values are left out.</summary>
    public static string EntityJson(string partitionKey, string rowKey, IEnumerable<(string Name, object? Value)> properties)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("PartitionKey", partitionKey);
            json.WriteString("RowKey", rowKey);
            foreach (var (name, value) in properties)
            {
                switch (value)
                {
                    case null or "":
                        break;
                    case string text:
                        json.WriteString(name, text);
                        break;
                    case int number:
                        json.WriteNumber(name, number);
                        break;
                    case long number:
                        json.WriteString(name, number.ToString(CultureInfo.InvariantCulture));
                        json.WriteString($"{name}@odata.type", "Edm.Int64");
                        break;
                    case double number:
                        json.WriteNumber(name, number);
                        json.WriteString($"{name}@odata.type", "Edm.Double");
                        break;
                    case bool flag:
                        json.WriteBoolean(name, flag);
                        break;
                    case DateTimeOffset time:
                        json.WriteString(name, time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                        json.WriteString($"{name}@odata.type", "Edm.DateTime");
                        break;
                    default:
                        json.WriteString(name, value.ToString());
                        break;
                }
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>"DOMAIN\user" of every session with a signed-in user (the installer runs as SYSTEM).</summary>
    public static IReadOnlyList<string> SessionUsers()
    {
        var users = new List<string>();
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var sessions, out var count))
        {
            return users;
        }
        try
        {
            var size = Marshal.SizeOf<SessionInfo>();
            for (var i = 0; i < count; i++)
            {
                var session = Marshal.PtrToStructure<SessionInfo>(sessions + (i * size));
                if (Query(session.SessionId, 5) is { Length: > 0 } user)
                {
                    users.Add(Query(session.SessionId, 7) is { Length: > 0 } domain ? $@"{domain}\{user}" : user);
                }
            }
        }
        finally
        {
            WTSFreeMemory(sessions);
        }
        return users.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        static string? Query(int sessionId, int infoClass)
        {
            if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _))
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
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SessionInfo
    {
        public int SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    static extern void WTSFreeMemory(IntPtr memory);
}
