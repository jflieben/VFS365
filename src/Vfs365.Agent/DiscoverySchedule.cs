using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Agent;

/// <summary>
/// When the mounted agent discovers and what a run means. A start with a recent state (under a week old, with libraries) shows it and
/// waits 5 to 90 minutes, so a tenant's sign-ins don't all discover at once; any other start discovers at once. Then daily, plus up to
/// 2 hours. A run whose search failed, also partly, is a failure: its libraries are kept (nothing is hidden on an incomplete search),
/// its time isn't (the next start doesn't take it as recent), and it is tried again in 15 to 60 minutes, sooner while there are no
/// libraries at all.
/// </summary>
public static class DiscoverySchedule
{
    public static readonly TimeSpan Daily = TimeSpan.FromDays(1), RecentState = TimeSpan.FromDays(7);

    /// <summary>
    /// A first start's state before any discovery: the OneDrive (if the user has one) and tenant root it just read. In OneDrive scope that
    /// is all discovery reads; with sites RunAt is MinValue (never discovered), so a start after an interrupted first run discovers at once.
    /// </summary>
    public static DiscoveryState Placeholder(MyHome home, string tenantHint, DriveScope scope, DateTimeOffset now) =>
        new(scope == DriveScope.OneDrive ? now : DateTimeOffset.MinValue, home.TenantRoot, [],
            new Dictionary<string, string>(), LibraryRules.Version, home.OneDrive, default, tenantHint);

    /// <summary>
    /// Whether a saved state can be shown at the start: it holds the tenant's SharePoint root and, unless the user has no OneDrive, their
    /// OneDrive. A user without a OneDrive is a state with a root and no OneDrive (earlier versions always saved one).
    /// </summary>
    public static bool Usable(DiscoveryState? saved, string tenantHint) =>
        saved is { } && (saved.OneDrive is not null || !string.IsNullOrEmpty(saved.TenantRoot)) && saved.TenantHint == tenantHint;

    /// <summary>A state the start may show a while before discovering: under a week old and, unless only the OneDrive shows, with libraries.</summary>
    public static bool Recent(DiscoveryState state, DriveScope scope, DateTimeOffset now) =>
        now - state.RunAt < RecentState && (scope == DriveScope.OneDrive || state.Libraries.Count > 0);

    public static TimeSpan StartWait(bool recent, Random random) => recent ? TimeSpan.FromSeconds(random.Next(5 * 60, 90 * 60 + 1)) : TimeSpan.Zero;

    public static DateTimeOffset NextDay(DateTimeOffset now, Random random) => now + Daily + TimeSpan.FromMinutes(random.Next(0, 121));

    /// <summary>
    /// The wait after a failed run, <paramref name="failuresInARow"/> counting this one: 15 to 60 minutes. Without libraries (a first
    /// run) 2 to 5 minutes, doubling with each failure in a row up to the same range.
    /// </summary>
    public static TimeSpan AfterFailure(int failuresInARow, bool hasLibraries, Random random)
    {
        var doubling = Math.Clamp(failuresInARow - 1, 0, 5);
        var (low, high) = hasLibraries ? (15, 60) : (Math.Min(15, 2 << doubling), Math.Min(60, 5 << doubling));
        return TimeSpan.FromSeconds(random.Next(low * 60, high * 60 + 1));
    }

    /// <summary>The state to keep after a run: its own, but after a failed search with the last run's time (MinValue when there was none).</summary>
    public static DiscoveryState Keep(DiscoveryResult result, DiscoveryState? previous) =>
        result.SearchError is null ? result.State : result.State with { RunAt = previous?.RunAt ?? DateTimeOffset.MinValue };
}
