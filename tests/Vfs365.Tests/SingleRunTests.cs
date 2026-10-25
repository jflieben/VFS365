using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Layout runs one at a time, and a request during a run makes it go once more instead of being dropped.</summary>
public sealed class SingleRunTests
{
    [Fact]
    public async Task Requests_during_a_run_make_it_go_once_more()
    {
        var first = new TaskCompletionSource();
        var runs = 0;
        var run = new SingleRun(async _ =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                await first.Task;
            }
        }, e => throw e);

        var running = run.RunAsync(default);
        await run.RunAsync(default); // after discovery
        await run.RunAsync(default); // the network is back
        first.SetResult();
        await running;

        Assert.Equal(2, runs);
        Assert.True(run.WaitIdle(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task A_cancelled_run_ends_quietly_and_nothing_runs_after_it()
    {
        using var stop = new CancellationTokenSource();
        var runs = 0;
        var failures = new List<Exception>();
        var run = new SingleRun(async ct =>
        {
            runs++;
            await stop.CancelAsync();
            ct.ThrowIfCancellationRequested();
        }, failures.Add);

        await run.RunAsync(stop.Token);
        await run.RunAsync(stop.Token);

        Assert.Equal(1, runs);
        Assert.Empty(failures);
    }

    [Fact]
    public async Task A_failure_is_reported_and_the_next_request_runs_again()
    {
        var runs = 0;
        var failures = new List<Exception>();
        var run = new SingleRun(_ => ++runs == 1 ? throw new IOException("locked") : Task.CompletedTask, failures.Add);

        await run.RunAsync(default);
        await run.RunAsync(default);

        Assert.Equal("locked", Assert.Single(failures).Message);
        Assert.Equal(2, runs);
    }
}
