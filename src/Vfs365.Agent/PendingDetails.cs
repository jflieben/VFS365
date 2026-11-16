using System.Collections.Concurrent;
using Vfs365.Core;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Files saved with required columns empty, until they are filled in: the tray's "Details needed" item opens the oldest, and reminders
/// come <see cref="FirstReminder"/> after the save and then at doubling gaps, <see cref="Reminders"/> in all (after 1, 3, 7 and 15
/// hours). Before a reminder the file is asked about again with one background call (filled in the browser, deleted, or the columns
/// no longer required: it leaves the list), at most <see cref="MaxChecks"/> files per round; the others count as still waiting. Kept
/// in memory, at most <see cref="MaxFiles"/> and for <see cref="KeptFor"/>: a later save adds a file again.
/// </summary>
sealed class PendingDetails(Func<string, string, CancellationToken, Task<IReadOnlyList<string>?>> missing, Action<DetailsRequest> show,
    Action<string, string, Action?> notify, Action<string> log, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan FirstReminder = TimeSpan.FromHours(1);
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(1);
    public const int Reminders = 4;
    public const int MaxChecks = 20;
    public const int MaxFiles = 200;

    /// <param name="Sent">Reminders so far; none are due once it reaches <see cref="Reminders"/>.</param>
    sealed record Entry(FileNeedsDetails File, DateTimeOffset Saved, DateTimeOffset NextReminder, int Sent = 0);

    readonly ConcurrentDictionary<string, Entry> files = new(StringComparer.Ordinal);
    int reminding;

    DateTimeOffset Now => (clock ?? (() => DateTimeOffset.UtcNow))();

    static string Key(string driveId, string itemId) => $"{driveId}|{itemId}";

    public int Count => files.Count;

    /// <summary>A save left these columns empty: the first reminder comes an hour from now. A full list lets its oldest file go.</summary>
    public void Add(FileNeedsDetails file)
    {
        if (file.Columns.Count == 0)
        {
            return;
        }
        files[Key(file.DriveId, file.ItemId)] = new Entry(file, Now, Now + FirstReminder);
        while (files.Count > MaxFiles && files.MinBy(f => f.Value.Saved) is { Key: not null } oldest)
        {
            files.TryRemove(oldest);
        }
    }

    /// <summary>The details window saved the file with nothing required left empty.</summary>
    public void Filled(string driveId, string itemId) => files.TryRemove(Key(driveId, itemId), out _);

    /// <summary>The tray item: the file waiting longest.</summary>
    public void ShowOldest()
    {
        if (files.Values.OrderBy(e => e.Saved).FirstOrDefault() is { } oldest)
        {
            show(Request(oldest.File));
        }
    }

    static DetailsRequest Request(FileNeedsDetails file) => new(file.DriveId, file.ItemId, file.Name, null, false, file.Columns);

    /// <summary>From the agent's tick: asks about the files that are due, and reminds about those still missing details in one notification.</summary>
    public async Task RemindAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref reminding, 1) == 1)
        {
            return;
        }
        try
        {
            RequestPriority.MarkBackground(); // these reads wait while Microsoft 365 throttles, and use the background's share of the budget
            var now = Now;
            foreach (var (key, entry) in files.Where(f => now - f.Value.Saved >= KeptFor).ToList())
            {
                files.TryRemove(new KeyValuePair<string, Entry>(key, entry));
            }
            var still = new List<FileNeedsDetails>();
            var checks = 0;
            foreach (var (key, entry) in files.Where(f => f.Value.Sent < Reminders && f.Value.NextReminder <= now).OrderBy(f => f.Value.NextReminder).ToList())
            {
                IReadOnlyList<string>? empty = null;
                if (checks++ < MaxChecks)
                {
                    try
                    {
                        empty = await missing(entry.File.DriveId, entry.File.ItemId, ct);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException && !ct.IsCancellationRequested)
                    {
                        empty = e is NoListItemException or RemoteException { Error: RemoteError.NotFound or RemoteError.Gone } ? [] : null;
                        log($"{entry.File.Name}: checking its required columns: {e.Message}");
                    }
                }
                if (empty is { Count: 0 })
                {
                    files.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                    continue;
                }
                var file = empty is null ? entry.File : entry.File with { Columns = empty };
                var next = entry with { File = file, Sent = entry.Sent + 1, NextReminder = now + FirstReminder * Math.Pow(2, entry.Sent + 1) };
                // Only the entry read: a save from the window (Filled) or a new save (Add) meanwhile wins
                if (files.TryUpdate(key, next, entry))
                {
                    still.Add(file);
                }
            }
            if (still.Count == 1)
            {
                notify(UiText.Get("Details.Title"), UiText.Get("Details.Reminder", still[0].Name), () => show(Request(still[0])));
            }
            else if (still.Count > 1)
            {
                notify(UiText.Get("Details.Title"), UiText.Get("Details.Reminders", still.Count), ShowOldest);
            }
        }
        finally
        {
            Volatile.Write(ref reminding, 0);
        }
    }
}
