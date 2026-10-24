using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Usage per local day and device for monitoring, kept in %LOCALAPPDATA%\VFS365\statistics.json until the day is over and sent, so
/// stops, sign-outs and failed sends lose nothing. The file roams with the profile on pooled hosts (FSLogix), so each day records the
/// device it happened on and is sent as that device. A day whose sends failed for a week is dropped. Thread-safe.
/// </summary>
public sealed class UsageStatistics(string filePath)
{
    /// <summary>Folds come every minute; a longer gap is sleep or hibernation and counts as this much running time at most.</summary>
    public static readonly TimeSpan MaxFoldGap = TimeSpan.FromMinutes(3);

    /// <summary>How long a day is tried again after its first failed send.</summary>
    public static readonly TimeSpan KeepTrying = TimeSpan.FromDays(7);

    public sealed class Day
    {
        public long GraphRequests { get; set; }
        public long SharePointRequests { get; set; }
        public long ResourceUnits { get; set; }
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public long Throttled { get; set; }
        public long ThrottledSeconds { get; set; }
        public long BudgetWaitSeconds { get; set; }
        public long BudgetWaitForegroundSeconds { get; set; }
        public long BudgetWaitBackgroundSeconds { get; set; }
        public int Errors { get; set; }
        public double MinutesRunning { get; set; }

        /// <summary>The last signed-in account (UPN) and VFS365 version of that day, kept for a send before the first token or after an update.</summary>
        public string? User { get; set; }
        public string? Version { get; set; }

        /// <summary>The device the usage happened on; null in files of earlier versions (sent as the current device).</summary>
        public string? DeviceName { get; set; }
        public string? DeviceId { get; set; }
        public string? EntraDeviceId { get; set; }

        /// <summary>Random, for the row key (Monitoring.StatisticsAsync); null in files of earlier versions, whose row keys had none.</summary>
        public string? Salt { get; set; }

        /// <summary>When a send of this day first failed (local time).</summary>
        public DateTime? FirstFailed { get; set; }

        public DeviceInfo DeviceOr(DeviceInfo current) => DeviceId is null ? current : new(DeviceName ?? DeviceId, DeviceId, EntraDeviceId);
    }

    readonly string filePath = filePath;
    readonly Lock gate = new(), saving = new();
    Dictionary<string, Day> days = new(StringComparer.Ordinal);
    ClientUsage seen;
    int seenErrors;
    DateTime? lastFold;

    public static string DefaultPath => Path.Combine(DiscoveryStore.DataDirectory, "statistics.json");

    /// <summary>By key: the day ("2026-10-06"), then "|" and the device ID when the day recorded one.</summary>
    public IReadOnlyDictionary<string, Day> Days
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, Day>(days, StringComparer.Ordinal);
            }
        }
    }

    public static UsageStatistics Load(string? path = null)
    {
        var statistics = new UsageStatistics(path ?? DefaultPath);
        try
        {
            if (File.Exists(statistics.filePath) && JsonSerializer.Deserialize<Dictionary<string, Day>>(File.ReadAllText(statistics.filePath)) is { } saved)
            {
                statistics.days = new(saved, StringComparer.Ordinal);
            }
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
        }
        return statistics;
    }

    static string DayOf(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    static string Key(string day, string? deviceId) => deviceId is null ? day : $"{day}|{deviceId}";

    /// <summary>The day of a key in <see cref="Days"/>.</summary>
    public static string DayOfKey(string key) => key.Split('|')[0];

    /// <summary>
    /// Adds what the client and the error count did since the last call to the day of <paramref name="localNow"/> on
    /// <paramref name="device"/>. A day of an earlier version without a device becomes this device's.
    /// </summary>
    public void Fold(ClientUsage usage, int errors, DateTime localNow, string? user = null, string? version = null, DeviceInfo? device = null)
    {
        lock (gate)
        {
            var delta = usage.Since(seen);
            var key = Key(DayOf(localNow), device?.Id);
            if (!days.TryGetValue(key, out var day))
            {
                if (device is not null && days.Remove(DayOf(localNow), out var legacy))
                {
                    day = legacy; // keeps its row key without a salt, which a send of the earlier version may have used
                }
                else
                {
                    day = new Day { Salt = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) };
                }
                (day.DeviceName, day.DeviceId, day.EntraDeviceId) = (device?.Name, device?.Id, device?.EntraDeviceId);
                days[key] = day;
            }
            day.User = user ?? day.User;
            day.Version = version ?? day.Version;
            day.GraphRequests += delta.GraphRequests;
            day.SharePointRequests += delta.SharePointRequests;
            day.ResourceUnits += delta.ResourceUnits;
            day.BytesReceived += delta.BytesReceived;
            day.BytesSent += delta.BytesSent;
            day.Throttled += delta.Throttled;
            day.ThrottledSeconds += delta.ThrottledSeconds;
            day.BudgetWaitForegroundSeconds += delta.BudgetWaitForegroundSeconds;
            day.BudgetWaitBackgroundSeconds += delta.BudgetWaitBackgroundSeconds;
            day.BudgetWaitSeconds += delta.BudgetWaitForegroundSeconds + delta.BudgetWaitBackgroundSeconds;
            day.Errors += errors - seenErrors;
            if (lastFold is { } last)
            {
                day.MinutesRunning += Math.Clamp((localNow - last).TotalMinutes, 0, MaxFoldGap.TotalMinutes);
            }
            (seen, seenErrors, lastFold) = (usage, errors, localNow);
        }
    }

    /// <summary>Keys of days before the day of <paramref name="localNow"/>, ready to send.</summary>
    public IReadOnlyList<string> Completed(DateTime localNow)
    {
        lock (gate)
        {
            return days.Keys.Where(k => string.CompareOrdinal(DayOfKey(k), DayOf(localNow)) < 0).Order(StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>The most recent day's signed-in account (UPN), for reports before this start's first token (a crash).</summary>
    public string? LastUser()
    {
        lock (gate)
        {
            return days.OrderByDescending(d => d.Key, StringComparer.Ordinal).Select(d => d.Value.User).FirstOrDefault(u => u is not null);
        }
    }

    public void Remove(string key)
    {
        lock (gate)
        {
            days.Remove(key);
        }
    }

    /// <summary>
    /// Sends the finished days; a day goes when <paramref name="send"/> (day, usage) says it arrived, or when its sends failed for
    /// <see cref="KeepTrying"/>. Returns how many did not arrive.
    /// </summary>
    public async Task<int> SendCompletedAsync(DateTime localNow, Func<string, Day, Task<bool>> send)
    {
        var failed = 0;
        foreach (var key in Completed(localNow))
        {
            Day? usage;
            lock (gate)
            {
                usage = days.GetValueOrDefault(key);
            }
            if (usage is null)
            {
                continue;
            }
            if (await send(DayOfKey(key), usage))
            {
                Remove(key);
                continue;
            }
            failed++;
            lock (gate)
            {
                usage.FirstFailed ??= localNow;
                if (localNow - usage.FirstFailed >= KeepTrying)
                {
                    days.Remove(key);
                }
            }
        }
        Save();
        return failed;
    }

    public static IEnumerable<(string Name, object? Value)> Values(string day, Day usage) =>
    [
        ("Day", day), ("GraphRequests", usage.GraphRequests), ("SharePointRequests", usage.SharePointRequests),
        ("ResourceUnits", usage.ResourceUnits), ("BytesReceived", usage.BytesReceived), ("BytesSent", usage.BytesSent),
        ("Throttled", usage.Throttled), ("ThrottledSeconds", usage.ThrottledSeconds), ("BudgetWaitSeconds", usage.BudgetWaitSeconds),
        ("BudgetWaitForegroundSeconds", usage.BudgetWaitForegroundSeconds), ("BudgetWaitBackgroundSeconds", usage.BudgetWaitBackgroundSeconds),
        ("Errors", usage.Errors), ("MinutesRunning", (int)Math.Round(usage.MinutesRunning)),
    ];

    public void Save()
    {
        try
        {
            string json;
            lock (gate)
            {
                json = JsonSerializer.Serialize(days);
            }
            lock (saving)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                File.WriteAllText(filePath + ".tmp", json);
                File.Move(filePath + ".tmp", filePath, overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
