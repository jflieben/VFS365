namespace Vfs365.Drive.WinFsp;

/// <summary>
/// Answers to requests a callback left pending (STATUS_PENDING). WinFsp never cancels or times out a request once a dispatcher thread
/// took it: not for its IrpTimeout, nor when the caller cancels or exits; only the unmount ends it. A request never answered hangs its
/// caller, and everything that waits for its file, until the volume stops. So each gets exactly one answer, an error when working it
/// out throws; none after <see cref="Stop"/>, when WinFsp's buffers and native file system are gone.
/// </summary>
sealed class PendingAnswers(Func<Exception, int> status, Action<string>? log)
{
    readonly ReaderWriterLockSlim answering = new();
    bool stopped;

    /// <summary>At the unmount: drops answers still to come, after one being sent.</summary>
    public void Stop()
    {
        answering.EnterWriteLock();
        stopped = true;
        answering.ExitWriteLock();
    }

    /// <summary>
    /// When <paramref name="result"/> completes, <paramref name="fill"/> makes it the answer (copying into WinFsp's buffer, which
    /// happens only here) and <paramref name="send"/> sends it. A result or fill that throws is answered with its error and no bytes.
    /// Never throws.
    /// </summary>
    public async Task AnswerAsync(string what, ValueTask<(int Status, uint Bytes)> result, Func<(int Status, uint Bytes), (int Status, uint Bytes)> fill,
        Action<int, uint> send)
    {
        (int Status, uint Bytes) answer = default;
        Exception? failure = null;
        try
        {
            answer = await result;
        }
        catch (Exception e)
        {
            failure = e;
        }
        answering.EnterReadLock();
        try
        {
            if (stopped)
            {
                return;
            }
            if (failure is null)
            {
                try
                {
                    answer = fill(answer);
                }
                catch (Exception e)
                {
                    failure = e;
                }
            }
            if (failure is not null)
            {
                log?.Invoke($"error: answering {what}: {failure.GetType().Name}: {failure.Message}");
                answer = (status(failure), 0);
            }
            send(answer.Status, answer.Bytes);
        }
        catch (Exception e)
        {
            log?.Invoke($"error: answering {what}: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            answering.ExitReadLock();
        }
    }
}
