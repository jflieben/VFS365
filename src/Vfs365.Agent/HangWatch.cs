namespace Vfs365.Agent;

/// <summary>
/// Signs of life from the agent to its supervisor, as two named events of the session (no file: a full or read-only profile changes
/// nothing): the agent's tick sets one every second while the drive is mounted, and the other once it stops.
/// </summary>
public sealed class AliveSignal : IDisposable
{
    readonly EventWaitHandle beat, stopping;

    AliveSignal(int processId)
    {
        beat = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\VFS365.Alive.{processId}");
        stopping = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\VFS365.Stopping.{processId}");
    }

    /// <summary>The signal of the agent process <paramref name="processId"/> (made, or opened when the other side made it); null when Windows refuses.</summary>
    public static AliveSignal? TryCreate(int processId)
    {
        try
        {
            return new AliveSignal(processId);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            return null;
        }
    }

    public void Beat() => beat.Set();

    /// <summary>The agent stops: saving and the last uploads may take long, and are no hang.</summary>
    public void Stopping() => stopping.Set();

    /// <summary>True when the agent ticked since the last call.</summary>
    public bool TakeBeat() => beat.WaitOne(0);

    public bool IsStopping => stopping.WaitOne(0);

    public void Dispose()
    {
        beat.Dispose();
        stopping.Dispose();
    }
}

/// <summary>
/// Tells the supervisor when the agent hangs: it ticked before (so the drive is mounted), isn't stopping, and gave no sign of life
/// for <see cref="HungAfter"/> while this PC was awake. Asked every <see cref="Poll"/>; a longer gap between two calls (sleep, a
/// frozen session) starts the count again, as the agent didn't run then either.
/// </summary>
public sealed class HangWatch
{
    public static readonly TimeSpan Poll = TimeSpan.FromSeconds(30), HungAfter = TimeSpan.FromMinutes(10);

    DateTimeOffset lastPoll = DateTimeOffset.MinValue;
    bool ticked;
    TimeSpan quiet;

    public bool Hung(DateTimeOffset now, bool beat, bool stopping)
    {
        var gap = now - lastPoll;
        lastPoll = now;
        if (stopping)
        {
            ticked = false;
            return false;
        }
        if (beat)
        {
            (ticked, quiet) = (true, TimeSpan.Zero);
            return false;
        }
        if (!ticked)
        {
            return false; // starting, or waiting for the network or a sign-in
        }
        if (gap < TimeSpan.Zero || gap > Poll * 3)
        {
            quiet = TimeSpan.Zero;
            return false;
        }
        quiet += gap;
        return quiet >= HungAfter;
    }
}
