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
    readonly Func<TimeSpan, CancellationToken, Task> delay;
    double tokens;
    DateTimeOffset last;
    long foregroundTicks;
    long backgroundTicks;

    public RequestBudget(int perMinute, Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        PerMinute = Math.Max(0, perMinute);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.delay = delay ?? Task.Delay;
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
            Refill();
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

    /// <summary>
    /// Corrects what a request took once its real cost is known (x-ms-resource-unit): more is taken at once, so the balance can go
    /// below zero and later requests wait; less is given back.
    /// </summary>
    public void Adjust(int delta)
    {
        if (PerMinute == 0 || delta == 0)
        {
            return;
        }
        lock (gate)
        {
            Refill();
            tokens = Math.Min(PerMinute, tokens - delta);
        }
    }

    public Task TakeAsync(int cost, bool background, CancellationToken ct) => TakeAsync(cost, () => background, ct);

    /// <summary>
    /// Waits until <paramref name="cost"/> units are taken. <paramref name="isBackground"/> is read before every try, so work promoted
    /// while it waits takes from the whole budget; <paramref name="wake"/> (a promotion) ends a wait early.
    /// </summary>
    public async Task TakeAsync(int cost, Func<bool> isBackground, CancellationToken ct, Task? wake = null)
    {
        while (true)
        {
            var background = isBackground();
            if (TryTake(cost, background, out var wait))
            {
                return;
            }
            Interlocked.Add(ref background ? ref backgroundTicks : ref foregroundTicks, wait.Ticks);
            var waiting = delay(wait, ct);
            await (background && wake is not null ? Task.WhenAny(waiting, wake) : waiting);
            ct.ThrowIfCancellationRequested();
        }
    }

    void Refill()
    {
        var now = clock();
        tokens = Math.Min(PerMinute, tokens + (now - last).TotalMinutes * PerMinute);
        last = now;
    }
}
