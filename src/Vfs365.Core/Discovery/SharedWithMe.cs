namespace Vfs365.Core.Discovery;

/// <summary>
/// A file or folder another person shared with the user (Graph search), and where it is in the owner's drive. Path: '/'-separated from
/// the drive's root, the item's own name included, null while unknown. Name, ParentId and WebUrl as last seen, to notice a move or
/// rename. ReadOnly: the user may only view it (always for a file: files shared directly show read-only). CheckedAt: when Path and
/// ReadOnly were read, a first read counting as up to <see cref="SharedOptions.CheckJitter"/> earlier. Misses: successful searches in
/// a row that didn't return it.
/// </summary>
public sealed record SharedItem(
    string DriveId,
    string ItemId,
    string Name,
    bool IsFolder,
    string? Path = null,
    string? OwnerName = null,
    string? OwnerEmail = null,
    long Size = 0,
    DateTimeOffset Modified = default,
    string? WebUrl = null,
    string? ParentId = null,
    bool ReadOnly = false,
    DateTimeOffset? CheckedAt = null,
    int Misses = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Key => $"{DriveId}|{ItemId}";
}

/// <summary>One page of search hits; More: search has more after it.</summary>
public sealed record SharedSearchPage(IReadOnlyList<SharedItem> Items, bool More, int Hits);

/// <summary>Where a shared item is in its drive: its path (its own name included), its parent's ID and its SharePoint list item.</summary>
public sealed record SharedLocation(string Path, string Name, string? ParentId, bool IsFolder, string? SiteUrl = null, string? ListId = null, string? ListItemId = null);

/// <summary>An item whose lookup found nothing or failed: when it was tried last and how many times in a row. It isn't looked up again until a back-off has passed.</summary>
public sealed record SharedLookupFailure(string Key, DateTimeOffset TriedAt, int Failures);

/// <summary>
/// The shared list as kept between runs. RunAt: when the last search that worked ended (MinValue: never). Failed: lookups that found
/// nothing or failed (null in state of earlier versions).
/// </summary>
public sealed record SharedState(DateTimeOffset RunAt, IReadOnlyList<SharedItem> Items, IReadOnlyList<SharedLookupFailure>? Failed = null);

/// <summary>Calls the shared list needs. Implemented in Vfs365.Graph.</summary>
public interface ISharedWithMeApi
{
    /// <summary>Items shared with <paramref name="user"/> (a UPN) by other people, search results <paramref name="from"/> on.</summary>
    Task<SharedSearchPage> SearchAsync(string user, int from, int size, CancellationToken ct);

    /// <summary>The item's place in its drive; null when it is gone or no longer shared with the user.</summary>
    Task<SharedLocation?> GetLocationAsync(string driveId, string itemId, CancellationToken ct);

    /// <summary>Whether the user may change the item (and add to a folder); null when that can't be told.</summary>
    Task<bool?> CanEditAsync(SharedLocation location, CancellationToken ct);
}

/// <summary>
/// Errors: items whose place couldn't be read this run (kept as they were, or left out when new). Deferred: items due but left for a
/// later run, over the per-run cap or after a string of failed calls.
/// </summary>
public sealed record SharedResult(SharedState State, int Hits, string? SearchError, IReadOnlyList<string> Errors, int Deferred = 0);

public sealed record SharedOptions
{
    /// <summary>One search call holds this many hits: a normal user's shares fit in one.</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>Hits read at most per run; more are left out (and the log says so).</summary>
    public int MaxItems { get; init; } = 1000;

    /// <summary>An item's path and rights are read again after this long (or after a move or rename search reports).</summary>
    public TimeSpan CheckMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// An item read for the first time counts as read up to this much earlier (a fixed share per user and item), so the items of a
    /// first run, or of users who start on the same day, don't all expire at the same moment. Under CheckMaxAge.
    /// </summary>
    public TimeSpan CheckJitter { get; init; } = TimeSpan.FromDays(6);

    /// <summary>
    /// Items looked up per run at most (a lookup is a Graph call, plus a SharePoint call for a folder's rights): changed items first,
    /// then new ones (recently modified first), then those checked longest ago. The rest wait for a later run.
    /// </summary>
    public int MaxLookupsPerRun { get; init; } = 100;

    /// <summary>
    /// An item whose lookup found nothing or failed is not looked up again for this long, then for 4 times as long after each further
    /// failure in a row, up to CheckMaxAge.
    /// </summary>
    public TimeSpan FailureBackoff { get; init; } = TimeSpan.FromHours(6);

    /// <summary>A run stops looking up after this many failed calls in a row (Graph or SharePoint unwell); the rest wait for a later run.</summary>
    public int StopAfterFailures { get; init; } = 8;

    /// <summary>Items looked up at the same time.</summary>
    public int Parallel { get; init; } = 4;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// Reads what other people shared with the user: Graph search for driveItems shared with the user's UPN, paged; items in the user's own
/// OneDrive are left out. New items, items moved or renamed (also a folder above them: their web URL changed) and those checked more than
/// CheckMaxAge ago are looked up for their path in the owner's drive and the user's rights; the rest reuse the last run's answer, so a
/// routine run is one search call. A run looks up at most <see cref="SharedOptions.MaxLookupsPerRun"/> items (the rest wait for a later
/// run), a first read counts as made up to CheckJitter earlier so the weekly re-checks spread out, and an item whose lookup found
/// nothing or failed waits out a back-off first. An item search doesn't return goes at the second successful run in a row without it
/// (one short answer doesn't hide anything). Covers sharing with named people only: "anyone" and organization links the user merely
/// opened aren't recorded as shared with the user.
/// </summary>
public sealed class SharedWithMeService(ISharedWithMeApi api, SharedOptions? options = null)
{
    readonly SharedOptions options = options ?? new SharedOptions();

    /// <param name="recheck">Read every item's path and rights again (vfs365 shared).</param>
    public async Task<SharedResult> RunAsync(string user, string ownDriveId, SharedState? previous, bool recheck, CancellationToken ct)
    {
        var before = (previous?.Items ?? []).ToDictionary(i => i.Key, StringComparer.Ordinal);
        var found = new Dictionary<string, SharedItem>(StringComparer.Ordinal);
        var hits = 0;
        try
        {
            for (var from = 0; from < options.MaxItems;)
            {
                var page = await api.SearchAsync(user, from, Math.Min(options.PageSize, options.MaxItems - from), ct);
                hits += page.Hits;
                foreach (var item in page.Items.Where(i => !i.DriveId.Equals(ownDriveId, StringComparison.Ordinal)))
                {
                    found.TryAdd(item.Key, item);
                }
                if (!page.More || page.Hits == 0)
                {
                    break;
                }
                from += page.Hits;
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            return new SharedResult(previous ?? new SharedState(DateTimeOffset.MinValue, []), hits, e.Message, []);
        }

        var now = options.Clock();
        var errors = new List<string>();
        var items = new List<SharedItem>();
        var failed = new List<SharedLookupFailure>();
        var failedBefore = (previous?.Failed ?? []).DistinctBy(f => f.Key).ToDictionary(f => f.Key, StringComparer.Ordinal);

        var deferred = 0;

        // Not looked up this run: a known item stays as it was, so a move or rename is still seen at the next run
        void Wait(Pending waiting, bool counted = true)
        {
            if (counted)
            {
                deferred++;
            }
            if (waiting.Stale is { } stale)
            {
                items.Add(stale);
            }
            if (waiting.Failure is not null)
            {
                failed.Add(waiting.Failure);
            }
        }

        // What each hit needs: nothing (the last answer stands), a lookup, or a wait because the last lookup failed
        var pending = new List<Pending>();
        foreach (var hit in found.Values)
        {
            var known = before.GetValueOrDefault(hit.Key);
            if (ReasonFor(known, hit, now, recheck) is not { } reason)
            {
                items.Add(hit with { Path = known!.Path, ReadOnly = known.ReadOnly, CheckedAt = known.CheckedAt });
                continue;
            }
            var wants = new Pending(hit, known, failedBefore.GetValueOrDefault(hit.Key), reason);
            if (wants.Failure is { } failure && !recheck && now - failure.TriedAt is var waited && waited >= TimeSpan.Zero && waited < Backoff(failure.Failures))
            {
                Wait(wants, counted: false);
                continue;
            }
            pending.Add(wants);
        }

        // Changed items first (their path is wrong now), then new ones, newest first (nothing shows for them yet), then the oldest checks
        var ordered = pending.OrderBy(p => p.Reason)
            .ThenBy(p => p.Reason == Reason.Expired ? p.Known!.CheckedAt!.Value : default)
            .ThenByDescending(p => p.Reason == Reason.New ? p.Hit.Modified : default)
            .ThenBy(p => p.Hit.Key, StringComparer.Ordinal)
            .ToList();
        var limit = recheck ? ordered.Count : Math.Max(0, options.MaxLookupsPerRun);
        foreach (var waiting in ordered.Skip(limit))
        {
            Wait(waiting);
        }

        var inARow = 0; // failed calls in a row
        using var slots = new SemaphoreSlim(Math.Max(1, options.Parallel));
        var lookups = ordered.Take(limit).Select(async p =>
        {
            await slots.WaitAsync(ct);
            try
            {
                if (Volatile.Read(ref inARow) >= options.StopAfterFailures)
                {
                    return new Looked(p, null, false, Skipped: true);
                }
                var location = await api.GetLocationAsync(p.Hit.DriveId, p.Hit.ItemId, ct);
                Volatile.Write(ref inARow, 0);
                if (location is null)
                {
                    return new Looked(p, null, false); // gone, or no longer shared: search lags behind
                }
                // A file shared directly shows read-only whatever the user's rights, so only a folder's are read
                var canEdit = location.IsFolder ? await api.CanEditAsync(location, ct) : false;
                var checkedAt = recheck || p.Known?.CheckedAt is not null ? now : now - options.CheckJitter * KeySpread.Share($"{user}|{p.Hit.Key}");
                return new Looked(p, p.Hit with
                {
                    Path = location.Path,
                    Name = location.Name,
                    IsFolder = location.IsFolder,
                    ParentId = location.ParentId ?? p.Hit.ParentId,
                    ReadOnly = canEdit == false,
                    CheckedAt = checkedAt,
                }, true);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref inARow);
                lock (errors)
                {
                    errors.Add($"{p.Hit.Name} ({p.Hit.Key}): {e.Message}");
                }
                return new Looked(p, p.Stale, false);
            }
            finally
            {
                slots.Release();
            }
        }).ToList();
        foreach (var looked in await Task.WhenAll(lookups))
        {
            if (looked.Skipped)
            {
                Wait(looked.Pending);
                continue;
            }
            if (looked.Item is not null)
            {
                items.Add(looked.Item);
            }
            if (!looked.Placed)
            {
                failed.Add(new SharedLookupFailure(looked.Pending.Hit.Key, now, (looked.Pending.Failure?.Failures ?? 0) + 1));
            }
        }
        // Not returned this time: kept once more, so one short search answer hides nothing
        foreach (var missed in before.Values.Where(i => !found.ContainsKey(i.Key) && i.Misses < 1 && i.Path is not null))
        {
            items.Add(missed with { Misses = missed.Misses + 1 });
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        failed.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return new SharedResult(new SharedState(now, items, failed), hits, null, errors, deferred);
    }

    /// <summary>Why a lookup is due, changed first; Changed, New, Expired in the order they are looked up.</summary>
    enum Reason { Changed, New, Expired }

    /// <summary>A hit that needs a lookup. Known: the last run's item; Failure: its last failed lookup.</summary>
    sealed record Pending(SharedItem Hit, SharedItem? Known, SharedLookupFailure? Failure, Reason Reason)
    {
        /// <summary>What shows until the lookup is done: the last run's item as it was (name, parent and web URL too, so the change is still seen next time); none for a new one.</summary>
        public SharedItem? Stale => Known is { Path: not null } ? Known with { Misses = 0 } : null;
    }

    /// <summary>Placed: the lookup answered with the item's place. Skipped: not tried, after too many failed calls in a row.</summary>
    sealed record Looked(Pending Pending, SharedItem? Item, bool Placed, bool Skipped = false);

    /// <summary>Why a hit is looked up; null when the last run's answer stands. The web URL holds the whole path: it changes when a folder above the item moves or is renamed.</summary>
    Reason? ReasonFor(SharedItem? known, SharedItem hit, DateTimeOffset now, bool recheck)
    {
        if (known is not { Path: not null, CheckedAt: { } checkedAt })
        {
            return Reason.New;
        }
        if (known.Name != hit.Name || (hit.ParentId is not null && known.ParentId != hit.ParentId) || (hit.WebUrl is not null && known.WebUrl != hit.WebUrl))
        {
            return Reason.Changed;
        }
        return recheck || now - checkedAt >= options.CheckMaxAge ? Reason.Expired : null;
    }

    /// <summary>How long after a failed lookup the item waits: FailureBackoff, 4 times as long for each failure in a row, at most CheckMaxAge.</summary>
    TimeSpan Backoff(int failures) =>
        TimeSpan.FromTicks((long)Math.Min(options.CheckMaxAge.Ticks, options.FailureBackoff.Ticks * Math.Pow(4, Math.Clamp(failures, 1, 10) - 1)));
}
