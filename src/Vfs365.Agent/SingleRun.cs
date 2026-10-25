namespace Vfs365.Agent;

/// <summary>
/// Work that runs one at a time, where a request during a run isn't lost: the run goes once more when it is done (folder redirection
/// and drive letters, asked for after discovery, when the network returns and at the first sign-in while a run may be going on).
/// </summary>
public sealed class SingleRun(Func<CancellationToken, Task> work, Action<Exception> failed)
{
    readonly SemaphoreSlim gate = new(1, 1);
    int again;

    /// <summary>Runs the work now, or once more after the run going on. Never throws: a failure goes to <c>failed</c>, a cancellation ends quietly.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }
        if (!await gate.WaitAsync(0, CancellationToken.None))
        {
            Volatile.Write(ref again, 1); // the run going on goes once more
            return;
        }
        try
        {
            do
            {
                Volatile.Write(ref again, 0);
                await work(ct);
            }
            while (Volatile.Read(ref again) == 1 && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            failed(e);
        }
        finally
        {
            gate.Release();
        }
        if (Volatile.Read(ref again) == 1 && !ct.IsCancellationRequested)
        {
            _ = Task.Run(() => RunAsync(ct)); // asked for just after the last look
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a run going on to end (unmount, after cancelling it); false when it still runs.</summary>
    public bool WaitIdle(TimeSpan timeout)
    {
        if (!gate.Wait(timeout))
        {
            return false;
        }
        gate.Release();
        return true;
    }
}
