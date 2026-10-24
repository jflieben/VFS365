namespace Vfs365.Core.Discovery;

/// <summary>Misses: libraries kept although this run didn't find them, with the number of runs in a row they were missing.</summary>
public sealed record Reconciliation(IReadOnlyList<LibraryEntry> Visible, string? HidingSkippedReason, IReadOnlyDictionary<string, int> Misses);

public static class DiscoveryReconciler
{
    /// <summary>Runs in a row a library must be missing before it is hidden.</summary>
    public const int MissesToHide = 2;

    /// <summary>Runs in a row a library must be missing before it is hidden while the set shrank by more than the safety ratio.</summary>
    public const int MissesToAcceptShrink = 3;

    /// <summary>
    /// The visible set after a run. Libraries that failed to evaluate keep their previous entry. A library missing from a run is hidden
    /// only when it was also missing from the run before (<see cref="MissesToHide"/>): one incomplete search answer doesn't wipe its
    /// cache. When search was incomplete nothing is hidden and no miss counts. When the set shrank by more than
    /// <paramref name="safetyRatio"/> (M365AutoLink's deletion circuit breaker), misses count but a library is hidden only after
    /// <see cref="MissesToAcceptShrink"/> runs in a row, so a real loss (a user who left most of their Teams) is accepted in the end.
    /// Libraries in <paramref name="goneAtOnce"/> (hidden by policy, site patterns and templates, or excluded by the library rules) go at
    /// once whatever their share. The reason is reported while it keeps a missing library.
    /// </summary>
    public static Reconciliation Reconcile(
        IReadOnlyList<LibraryEntry> previous,
        IReadOnlyList<LibraryEntry> found,
        IReadOnlySet<string> failedKeys,
        bool searchIncomplete,
        double safetyRatio,
        IReadOnlySet<string>? goneAtOnce = null,
        IReadOnlyDictionary<string, int>? previousMisses = null)
    {
        if (goneAtOnce is { Count: > 0 })
        {
            previous = previous.Where(old => !goneAtOnce.Contains(old.Key)).ToList();
        }
        var visible = found.ToDictionary(library => library.Key);
        foreach (var old in previous.Where(old => failedKeys.Contains(old.Key)))
        {
            visible.TryAdd(old.Key, old);
        }

        var missing = previous.Where(old => !visible.ContainsKey(old.Key)).ToList();
        var misses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (missing.Count == 0)
        {
            return new(visible.Values.ToList(), null, misses);
        }

        var shrank = !searchIncomplete && visible.Count < Math.Floor(previous.Count * (1 - safetyRatio));
        var reason = searchIncomplete ? "SharePoint Search returned incomplete results"
            : shrank ? $"the library set shrank from {previous.Count} to {visible.Count}"
            : null;

        var kept = false;
        foreach (var old in missing)
        {
            var before = previousMisses?.GetValueOrDefault(old.Key) ?? 0;
            var count = searchIncomplete ? before : before + 1;
            if (searchIncomplete || count < (shrank ? MissesToAcceptShrink : MissesToHide))
            {
                visible.TryAdd(old.Key, old);
                kept = true;
                if (count > 0)
                {
                    misses[old.Key] = count;
                }
            }
        }
        return new(visible.Values.ToList(), kept ? reason : null, misses);
    }
}
