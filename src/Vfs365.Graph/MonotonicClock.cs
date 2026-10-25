using System.Diagnostics;

namespace Vfs365.Graph;

/// <summary>
/// Time since the process started, from Stopwatch: it never steps when the wall clock is set (w32time after resume, dual boot, a VM
/// restored), so pauses, budgets and reuse windows measured with it end on time.
/// </summary>
public static class MonotonicClock
{
    static readonly long start = Stopwatch.GetTimestamp();

    public static TimeSpan Now() => Stopwatch.GetElapsedTime(start);
}
