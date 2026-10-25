using Vfs365.Graph;

namespace Vfs365.Tests;

public sealed class PushDebounceTests
{
    /// <summary>Waits block until the test lets them go, moving the (monotonic) clock by their length.</summary>
    sealed class Waits
    {
        public TimeSpan Now = TimeSpan.FromHours(1);
        public List<(TimeSpan Wait, TaskCompletionSource Done)> Pending { get; } = [];

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ct.Register(() => done.TrySetCanceled(ct));
            Pending.Add((wait, done));
            return done.Task;
        }

        public void Elapse(int index)
        {
            Now += Pending[index].Wait;
            Pending[index].Done.SetResult();
        }
    }

    static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }
        Assert.True(condition());
    }

    [Fact]
    public async Task Notifications_join_one_read_after_the_settle_time_and_jitter()
    {
        var waits = new Waits();
        var reads = 0;
        using var debounce = new PushDebounce(() => { Interlocked.Increment(ref reads); return Task.CompletedTask; })
        {
            Monotonic = () => waits.Now, Random = () => 0.5, Delay = waits.Delay,
        };

        debounce.Notify();
        debounce.Notify();
        debounce.Notify();

        var wait = Assert.Single(waits.Pending).Wait;
        Assert.Equal(TimeSpan.FromSeconds(8), wait); // 3 s plus half of 10 s
        waits.Elapse(0);
        await Until(() => reads == 1);
    }

    [Fact]
    public async Task Reads_of_a_drive_are_at_least_the_minimum_interval_apart()
    {
        var waits = new Waits();
        var reads = 0;
        using var debounce = new PushDebounce(() => { Interlocked.Increment(ref reads); return Task.CompletedTask; })
        {
            Monotonic = () => waits.Now, Random = () => 0, Delay = waits.Delay,
        };

        debounce.Notify();
        waits.Elapse(0);
        await Until(() => reads == 1);

        debounce.Notify(); // right after the read
        Assert.Equal(TimeSpan.FromSeconds(30), waits.Pending[1].Wait);
        waits.Elapse(1);
        await Until(() => reads == 2);
    }

    [Fact]
    public async Task Nothing_is_read_after_the_channel_closes()
    {
        var waits = new Waits();
        var reads = 0;
        var debounce = new PushDebounce(() => { reads++; return Task.CompletedTask; }) { Monotonic = () => waits.Now, Random = () => 0, Delay = waits.Delay };

        debounce.Notify();
        debounce.Dispose();
        debounce.Notify();

        await Until(() => waits.Pending[0].Done.Task.IsCanceled);
        Assert.Single(waits.Pending);
        Assert.Equal(0, reads);
    }
}
