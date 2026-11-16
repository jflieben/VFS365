using System.Collections.Concurrent;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// Tells the user about a saved file with empty required columns (<see cref="DriveApi.NeedsDetails"/>): always in the log; as a notification
/// at most once per file per <see cref="NotifyEvery"/> and once per <see cref="NotifyGap"/> overall, so a copy into such a library isn't a
/// stream of them. A click opens the file's details window with those columns in bold.
/// </summary>
sealed class DetailsNotices(Action<DetailsRequest> show, Action<string, string, Action?> notify, Action<string> log, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan NotifyEvery = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan NotifyGap = TimeSpan.FromMinutes(1);

    readonly ConcurrentDictionary<string, DateTimeOffset> notified = new();
    readonly Lock gate = new();
    DateTimeOffset last = DateTimeOffset.MinValue;

    public void Handle(FileNeedsDetails file)
    {
        if (file.Columns.Count == 0)
        {
            log($"{file.Name}: left checked out after the save, could not read its required columns: {file.Error}");
            return;
        }
        var columns = string.Join(", ", file.Columns);
        log(file.StillCheckedOut
            ? $"{file.Name}: required columns are empty ({columns}); SharePoint left it checked out and the check-in failed: {file.Error}"
            : $"{file.Name}: required columns are empty ({columns})");

        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        var key = $"{file.DriveId}|{file.ItemId}";
        lock (gate)
        {
            if ((notified.TryGetValue(key, out var earlier) && now - earlier < NotifyEvery) || now - last < NotifyGap)
            {
                return;
            }
            notified[key] = last = now;
        }
        notify(UiText.Get(file.StillCheckedOut ? "CheckedOut.Title" : "Details.Title"), UiText.Get(file.StillCheckedOut ? "CheckedOut.Text" : "Details.Text", file.Name),
            () => show(new DetailsRequest(file.DriveId, file.ItemId, file.Name, null, false, file.Columns)));
    }
}
