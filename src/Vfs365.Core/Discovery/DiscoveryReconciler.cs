namespace Vfs365.Core.Discovery;

public sealed record Reconciliation(IReadOnlyList<LibraryEntry> Visible, string? HidingSkippedReason);

public static class DiscoveryReconciler
{
    /// <summary>
    /// The visible set after a run. Libraries that failed to evaluate keep their previous entry. Nothing is hidden when search
    /// was incomplete or the set shrank by more than <paramref name="safetyRatio"/> (M365AutoLink's deletion circuit breaker), except
    /// libraries hidden by policy (site patterns and templates): those go whatever their share.
    /// </summary>
    public static Reconciliation Reconcile(
        IReadOnlyList<LibraryEntry> previous,
        IReadOnlyList<LibraryEntry> found,
        IReadOnlySet<string> failedKeys,
        bool searchIncomplete,
        double safetyRatio,
        IReadOnlySet<string>? hiddenByPolicy = null)
    {
        if (hiddenByPolicy is { Count: > 0 })
        {
            previous = previous.Where(old => !hiddenByPolicy.Contains(old.Key)).ToList();
        }
        var visible = found.ToDictionary(library => library.Key);
        foreach (var old in previous.Where(old => failedKeys.Contains(old.Key)))
        {
            visible.TryAdd(old.Key, old);
        }

        if (!previous.Any(old => !visible.ContainsKey(old.Key)))
        {
            return new(visible.Values.ToList(), null);
        }

        string? reason = null;
        if (searchIncomplete)
        {
            reason = "SharePoint Search returned incomplete results";
        }
        else if (visible.Count < Math.Floor(previous.Count * (1 - safetyRatio)))
        {
            reason = $"the library set shrank from {previous.Count} to {visible.Count}";
        }

        if (reason is not null)
        {
            foreach (var old in previous)
            {
                visible.TryAdd(old.Key, old);
            }
        }
        return new(visible.Values.ToList(), reason);
    }
}
