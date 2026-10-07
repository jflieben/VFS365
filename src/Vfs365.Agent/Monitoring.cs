using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Vfs365.Agent;

/// <summary>Where reports go: an Azure table service URL with a SAS (policy MonitoringUrl).</summary>
public sealed record MonitoringTarget(string BaseUrl, string Query, DateTimeOffset? Expires)
{
    /// <summary>The storage account (host and path), without the SAS: safe to log.</summary>
    public string Account => BaseUrl[(BaseUrl.IndexOf("://", StringComparison.Ordinal) + 3)..];

    public Uri TableUri(string table) => new($"{BaseUrl}/{table}{Query}");

    /// <summary>Null when <paramref name="url"/> is empty (no reporting) or not usable (<paramref name="problem"/> says why).</summary>
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
        DateTimeOffset? expires = null;
        foreach (var part in uri.Query.TrimStart('?').Split('&'))
        {
            if (part.StartsWith("se=", StringComparison.OrdinalIgnoreCase)
                && DateTimeOffset.TryParse(Uri.UnescapeDataString(part[3..]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            {
                expires = at;
            }
        }
        return new($"{uri.GetLeftPart(UriPartial.Authority)}{uri.AbsolutePath.TrimEnd('/')}", uri.Query, expires);
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
/// per day (sent the day after), installs, updates and uninstalls. Fire and forget: nothing waits for a report, nothing is retried,
/// and the first failure is logged once.
/// </summary>
public sealed class Monitoring(MonitoringTarget target, DeviceInfo device, Action<string> log)
{
    public const string ErrorsTable = "errors";
    public const string StatisticsTable = "statistics";
    public const string DevicesTable = "devices";
    public const string Instructions = "https://github.com/jflieben/VFS365/blob/main/docs/DEPLOYMENT.md#monitoring";
    const int MaxErrorsPerHour = 20;

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };

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

    public MonitoringTarget Target => target;
    public DeviceInfo Device => device;

    /// <summary>The signed-in account (UPN) and its tenant, once known.</summary>
    public Func<string?> User { get; set; } = () => null;
    public Func<string?> TenantId { get; set; } = () => null;

    /// <summary>Errors seen, reported or not, for the day's statistics.</summary>
    public int Errors => Volatile.Read(ref errors);

    public string Started => $"Monitoring: errors and daily statistics to {target.Account}" +
        (target.Expires is { } expires ? $" (SAS valid until {expires.ToLocalTime():yyyy-MM-dd HH:mm})" : "");

    public void Error(string source, string message)
    {
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

    /// <summary>One row per day, device and Windows user.</summary>
    public void Statistics(string day, IEnumerable<(string Name, object? Value)> values) =>
        Track(SendAsync(StatisticsTable, day, $"{device.Id}_{WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName}", [.. Common(), .. values]));

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

    IEnumerable<(string, object?)> Common() =>
    [
        ("DeviceName", device.Name), ("DeviceId", device.Id), ("EntraDeviceId", device.EntraDeviceId), ("User", User()),
        ("WindowsUser", $@"{Environment.UserDomainName}\{Environment.UserName}"), ("TenantId", TenantId()), ("Version", Version),
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
        if (target.Expires is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            FailOnce($"the SAS expired on {expires.ToLocalTime():yyyy-MM-dd HH:mm}");
            return false;
        }
        try
        {
            var content = new StringContent(EntityJson(partitionKey, rowKey, properties), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, target.TableUri(table)) { Content = content };
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
            FailOnce($"{(int)response.StatusCode} {Explain(await ErrorCodeAsync(response).ConfigureAwait(false), table)}");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            FailOnce(e.Message);
        }
        return false;
    }

    void FailOnce(string detail)
    {
        if (Interlocked.Exchange(ref failed, 1) == 0)
        {
            log($"error: monitoring to {target.Account} failed: {detail}. Reports are not retried; see {Instructions}");
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
