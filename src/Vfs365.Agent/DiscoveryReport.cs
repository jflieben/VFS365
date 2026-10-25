using System.Text.Json;
using Vfs365.Core.Discovery;

namespace Vfs365.Agent;

/// <summary>A library discovery stopped showing because no run found it, and since when.</summary>
public sealed record HiddenLibrary(string Key, string WebUrl, string SiteTitle, string Title, string Reason, DateTimeOffset Since);

/// <summary>
/// What the last discovery left out and why, in %LOCALAPPDATA%\VFS365\discovery-report.json (discovery.json holds what shows): the
/// libraries skipped by a rule, the errors, and libraries hidden because runs in a row didn't find them (kept <see cref="KeepHidden"/>).
/// For `vfs365 why` and support bundles; the drive never reads it.
/// </summary>
public sealed record DiscoveryReport(DateTimeOffset At, int SearchHits, string? SearchError, string? SearchNote, string? HidingSkippedReason,
    IReadOnlyList<SkippedLibrary> Skipped, IReadOnlyList<SkippedLibrary> Errors, IReadOnlyList<HiddenLibrary> Hidden)
{
    public const string FileName = "discovery-report.json";
    public const int MaxEntries = 5000;
    public static readonly TimeSpan KeepHidden = TimeSpan.FromDays(30);
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath { get; } = Path.Combine(DiscoveryStore.DataDirectory, FileName);

    /// <param name="before">The state the run started from: its libraries that this run neither shows nor skipped were hidden by it.</param>
    /// <param name="earlier">The last report, whose hidden libraries stay listed while they don't show again.</param>
    public static DiscoveryReport From(DiscoveryResult result, DiscoveryState? before, DiscoveryReport? earlier, DateTimeOffset now)
    {
        var shown = result.Libraries.Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var accounted = result.Skipped.Concat(result.Errors).Select(s => s.Key).Concat(shown).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var hidden = new Dictionary<string, HiddenLibrary>(StringComparer.OrdinalIgnoreCase);
        foreach (var old in earlier?.Hidden ?? [])
        {
            if (now - old.Since < KeepHidden && !accounted.Contains(old.Key))
            {
                hidden[old.Key] = old;
            }
        }
        // Another tenant's state is no state of this one's runs
        if (result.SearchError is null && before is not null && string.Equals(before.TenantRoot, result.TenantRoot, StringComparison.OrdinalIgnoreCase))
        {
            foreach (var library in before.Libraries.Where(l => !accounted.Contains(l.Key)))
            {
                var runs = (before.Misses?.GetValueOrDefault(library.Key) ?? 0) + 1;
                hidden[library.Key] = new HiddenLibrary(library.Key, library.WebUrl, library.SiteTitle, library.LibraryTitle,
                    $"Not found by search (or a pinned location) in {runs} discovery runs in a row, so it was hidden", now);
            }
        }
        return new DiscoveryReport(now, result.SearchHits, result.SearchError, result.SearchNote, result.HidingSkippedReason,
            result.Skipped.Take(MaxEntries).ToList(), result.Errors.Take(MaxEntries).ToList(),
            hidden.Values.OrderBy(h => h.WebUrl, StringComparer.OrdinalIgnoreCase).Take(MaxEntries).ToList());
    }

    /// <summary>Builds the report of a run and saves it next to the discovery state.</summary>
    public static void Record(DiscoveryResult result, DiscoveryState? before) => Save(From(result, before, Load(), DateTimeOffset.UtcNow), FilePath);

    public static DiscoveryReport? Load(string? path = null)
    {
        try
        {
            path ??= FilePath;
            return File.Exists(path) ? JsonSerializer.Deserialize<DiscoveryReport>(File.ReadAllText(path), Json) : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(DiscoveryReport report, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(report, Json));
        File.Move(temporary, path, overwrite: true);
    }
}
