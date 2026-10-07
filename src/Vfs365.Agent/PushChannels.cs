using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// A push channel for each drive in active use (engine.ActiveDrives); a notification makes the engine read that drive's changes.
/// Channels close when a drive goes idle and reopen on its next use.
/// </summary>
sealed class PushChannels(DriveEngine engine, DriveNotifications notifications, Action<string> log) : IDisposable
{
    readonly Dictionary<string, CancellationTokenSource> open = new(StringComparer.Ordinal);

    public void Sync()
    {
        var active = engine.ActiveDrives().ToHashSet(StringComparer.Ordinal);
        lock (open)
        {
            foreach (var driveId in active.Where(d => !open.ContainsKey(d)))
            {
                var cancel = new CancellationTokenSource();
                open[driveId] = cancel;
                log($"push channel for drive {driveId}");
                _ = notifications.ListenAsync(driveId, () => _ = engine.PollAsync(driveId), up => engine.SetPush(driveId, up), cancel.Token);
            }
            foreach (var driveId in open.Keys.Where(d => !active.Contains(d)).ToList())
            {
                Close(driveId);
            }
        }
    }

    void Close(string driveId)
    {
        open[driveId].Cancel();
        open[driveId].Dispose();
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
