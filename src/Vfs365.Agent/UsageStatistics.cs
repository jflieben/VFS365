using System.Globalization;
using System.Text.Json;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Usage per local day for monitoring, kept in %LOCALAPPDATA%\VFS365\statistics.json until the day is over and sent, so stops and
/// sign-outs lose nothing. Days older than a week are dropped unsent.
/// </summary>
public sealed class UsageStatistics(string filePath)
{
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
        public int Errors { get; set; }
        public double MinutesRunning { get; set; }
    }

    readonly string filePath = filePath;
    Dictionary<string, Day> days = new(StringComparer.Ordinal);
    ClientUsage seen;
    int seenErrors;
    DateTime? lastFold;

    public static string DefaultPath => Path.Combine(DiscoveryStore.DataDirectory, "statistics.json");

    public IReadOnlyDictionary<string, Day> Days => days;

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
    public void Fold(ClientUsage usage, int errors, DateTime localNow)
    {
        var delta = usage.Since(seen);
        var day = days.TryGetValue(DayOf(localNow), out var known) ? known : days[DayOf(localNow)] = new Day();
        day.GraphRequests += delta.GraphRequests;
        day.SharePointRequests += delta.SharePointRequests;
        day.ResourceUnits += delta.ResourceUnits;
        day.BytesReceived += delta.BytesReceived;
        day.BytesSent += delta.BytesSent;
        day.Throttled += delta.Throttled;
        day.ThrottledSeconds += delta.ThrottledSeconds;
        day.BudgetWaitSeconds += delta.BudgetWaitSeconds;
        day.Errors += errors - seenErrors;
        if (lastFold is { } last)
        {
            day.MinutesRunning += Math.Max(0, (localNow - last).TotalMinutes);
        }
        (seen, seenErrors, lastFold) = (usage, errors, localNow);
        foreach (var old in days.Keys.Where(d => string.CompareOrdinal(d, DayOf(localNow.AddDays(-7))) < 0).ToList())
        {
            days.Remove(old);
        }
    }

    /// <summary>Days before the day of <paramref name="localNow"/>, ready to send.</summary>
    public IReadOnlyList<string> Completed(DateTime localNow) =>
        days.Keys.Where(d => string.CompareOrdinal(d, DayOf(localNow)) < 0).Order(StringComparer.Ordinal).ToList();

    public void Remove(string day) => days.Remove(day);

    public static IEnumerable<(string Name, object? Value)> Values(string day, Day usage) =>
    [
        ("Day", day), ("GraphRequests", usage.GraphRequests), ("SharePointRequests", usage.SharePointRequests),
        ("ResourceUnits", usage.ResourceUnits), ("BytesReceived", usage.BytesReceived), ("BytesSent", usage.BytesSent),
        ("Throttled", usage.Throttled), ("ThrottledSeconds", usage.ThrottledSeconds), ("BudgetWaitSeconds", usage.BudgetWaitSeconds),
        ("Errors", usage.Errors), ("MinutesRunning", (int)Math.Round(usage.MinutesRunning)),
    ];

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
            File.WriteAllText(filePath + ".tmp", JsonSerializer.Serialize(days));
            File.Move(filePath + ".tmp", filePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
