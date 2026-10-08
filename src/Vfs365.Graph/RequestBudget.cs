namespace Vfs365.Graph;

/// <summary>
/// This user's limit on SharePoint resource units per minute (zero: none), a token bucket that holds one minute's worth. Background
/// work only takes from the upper half, so requests users wait for go first. Microsoft's own limits apply per app per tenant, shared
/// by every user of the app (<see cref="ResourceUnits"/>).
/// </summary>
public sealed class RequestBudget
{
    readonly Lock gate = new();
    readonly Func<DateTimeOffset> clock;
    double tokens;
    DateTimeOffset last;
    long foregroundTicks;
    long backgroundTicks;

    public RequestBudget(int perMinute, Func<DateTimeOffset>? clock = null)
    {
        PerMinute = Math.Max(0, perMinute);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        tokens = PerMinute;
        last = this.clock();
    }

    public int PerMinute { get; }

    /// <summary>Time requests waited for the budget, added up over requests: those a user waits for, and background work.</summary>
    public TimeSpan WaitedForeground => TimeSpan.FromTicks(Interlocked.Read(ref foregroundTicks));
    public TimeSpan WaitedBackground => TimeSpan.FromTicks(Interlocked.Read(ref backgroundTicks));

    /// <summary>Takes <paramref name="cost"/> units when they are there; otherwise how long to wait before asking again.</summary>
    public bool TryTake(int cost, bool background, out TimeSpan wait)
    {
        wait = TimeSpan.Zero;
        if (PerMinute == 0 || cost <= 0)
        {
            return true;
        }
        lock (gate)
        {
            var now = clock();
            tokens = Math.Min(PerMinute, tokens + (now - last).TotalMinutes * PerMinute);
            last = now;
            var floor = background ? PerMinute / 2.0 : 0;
            if (tokens - cost >= floor || (tokens >= PerMinute && !background))
            {
                tokens -= cost;
                return true;
            }
            wait = TimeSpan.FromMinutes((floor + cost - tokens) / PerMinute);
            return false;
        }
    }

    public async Task TakeAsync(int cost, bool background, CancellationToken ct)
    {
        while (!TryTake(cost, background, out var wait))
        {
            Interlocked.Add(ref background ? ref backgroundTicks : ref foregroundTicks, wait.Ticks);
            await Task.Delay(wait, ct);
        }
    }
}
