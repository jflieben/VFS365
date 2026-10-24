using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// A push channel for each drive in active use whose change feed works (engine.PushDrives); a notification makes the engine read that
/// drive's changes, debounced per drive (<see cref="PushDebounce"/>). Channels close when a drive goes idle or its feed fails, and
/// reopen on its next use or once the feed works again.
/// </summary>
sealed class PushChannels(DriveEngine engine, DriveNotifications notifications, Action<string> log) : IDisposable
{
    readonly Dictionary<string, (CancellationTokenSource Cancel, PushDebounce Debounce)> open = new(StringComparer.Ordinal);

    public void Sync()
    {
        var active = engine.PushDrives().ToHashSet(StringComparer.Ordinal);
        lock (open)
        {
            foreach (var driveId in active.Where(d => !open.ContainsKey(d)))
            {
                var cancel = new CancellationTokenSource();
                var debounce = new PushDebounce(() => engine.NotifiedAsync(driveId));
                open[driveId] = (cancel, debounce);
                log($"push channel for drive {driveId}");
                _ = notifications.ListenAsync(driveId, debounce.Notify, up => engine.SetPush(driveId, up), cancel.Token);
            }
            foreach (var driveId in open.Keys.Where(d => !active.Contains(d)).ToList())
            {
                Close(driveId);
            }
        }
    }

    void Close(string driveId)
    {
        var (cancel, debounce) = open[driveId];
        debounce.Dispose();
        cancel.Cancel();
        cancel.Dispose();
        open.Remove(driveId);
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
