using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// A push channel for each drive in active use whose change feed works (engine.PushDrives); a notification makes the engine read that
/// drive's changes, debounced per drive (<see cref="PushDebounce"/>). Channels close when a drive goes idle or its feed fails, and
/// reopen on its next use or once the feed works again. A closed channel's listener may still be finishing: what it reports then
/// (up, down, a notification) is ignored, so it can't undo the state of the drive's next channel.
/// </summary>
sealed class PushChannels(DriveEngine engine, DriveNotifications notifications, Action<string> log) : IDisposable
{
    sealed class Channel(PushDebounce debounce)
    {
        public CancellationTokenSource Cancel { get; } = new();
        public PushDebounce Debounce { get; } = debounce;
    }

    readonly Dictionary<string, Channel> open = new(StringComparer.Ordinal);

    public void Sync()
    {
        var active = engine.PushDrives().ToHashSet(StringComparer.Ordinal);
        lock (open)
        {
            foreach (var driveId in active.Where(d => !open.ContainsKey(d)))
            {
                Channel? channel = null;
                channel = new Channel(new PushDebounce(() => Current(driveId, channel!) ? engine.NotifiedAsync(driveId) : Task.CompletedTask));
                open[driveId] = channel;
                log($"push channel for drive {driveId}");
                var listening = notifications.ListenAsync(driveId, channel.Debounce.Notify, up => SetPush(driveId, channel, up), channel.Cancel.Token);
                // The token stays usable until the listener is done with it
                _ = listening.ContinueWith(_ => channel.Cancel.Dispose(), TaskScheduler.Default);
            }
            foreach (var driveId in open.Keys.Where(d => !active.Contains(d)).ToList())
            {
                Close(driveId);
            }
        }
    }

    bool Current(string driveId, Channel channel)
    {
        lock (open)
        {
            return open.TryGetValue(driveId, out var current) && current == channel;
        }
    }

    void SetPush(string driveId, Channel channel, bool up)
    {
        lock (open)
        {
            if (open.TryGetValue(driveId, out var current) && current == channel)
            {
                engine.SetPush(driveId, up);
            }
        }
    }

    void Close(string driveId)
    {
        var channel = open[driveId];
        open.Remove(driveId);
        channel.Debounce.Dispose();
        try
        {
            channel.Cancel.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // the listener ended by itself
        }
        engine.SetPush(driveId, false);
    }

    public void Dispose()
    {
        lock (open)
        {
            foreach (var driveId in open.Keys.ToList())
            {
                Close(driveId);
            }
        }
    }
}
