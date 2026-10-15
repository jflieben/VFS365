using System.Globalization;
using System.Text.Json;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Usage per local day for monitoring, kept in %LOCALAPPDATA%\VFS365\statistics.json until the day is over and sent, so stops,
/// sign-outs and failed sends lose nothing. Days older than a week are dropped unsent. Thread-safe.
/// </summary>
public sealed class UsageStatistics(string filePath)
{
    /// <summary>Folds come every minute; a longer gap is sleep or hibernation and counts as this much running time at most.</summary>
    public static readonly TimeSpan MaxFoldGap = TimeSpan.FromMinutes(3);

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
    }

    readonly string filePath = filePath;
    readonly Lock gate = new(), saving = new();
    Dictionary<string, Day> days = new(StringComparer.Ordinal);
    ClientUsage seen;
    int seenErrors;
    DateTime? lastFold;

    public static string DefaultPath => Path.Combine(DiscoveryStore.DataDirectory, "statistics.json");

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
        catch (Exception e) when (e is JsonException or IOException)
        {
        }
        return statistics;
    }

    static string DayOf(DateTime local) => local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Adds what the client and the error count did since the last call to the day of <paramref name="localNow"/>.</summary>
    public void Fold(ClientUsage usage, int errors, DateTime localNow, string? user = null, string? version = null)
    {
        lock (gate)
        {
            var delta = usage.Since(seen);
            var day = days.TryGetValue(DayOf(localNow), out var known) ? known : days[DayOf(localNow)] = new Day();
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
            foreach (var old in days.Keys.Where(d => string.CompareOrdinal(d, DayOf(localNow.AddDays(-7))) < 0).ToList())
            {
                days.Remove(old);
            }
        }
    }

    /// <summary>Days before the day of <paramref name="localNow"/>, ready to send.</summary>
    public IReadOnlyList<string> Completed(DateTime localNow)
    {
        lock (gate)
        {
            return days.Keys.Where(d => string.CompareOrdinal(d, DayOf(localNow)) < 0).Order(StringComparer.Ordinal).ToList();
        }
    }

    public void Remove(string day)
    {
        lock (gate)
        {
            days.Remove(day);
        }
    }

    /// <summary>Sends the finished days; a day goes only when <paramref name="send"/> says it arrived. Returns how many did not.</summary>
    public async Task<int> SendCompletedAsync(DateTime localNow, Func<string, Day, Task<bool>> send)
    {
        var failed = 0;
        foreach (var day in Completed(localNow))
        {
            Day? usage;
            lock (gate)
            {
                usage = days.GetValueOrDefault(day);
            }
            if (usage is null)
            {
                continue;
            }
            if (await send(day, usage))
            {
                Remove(day);
            }
            else
            {
                failed++;
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
