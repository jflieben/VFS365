namespace Vfs365.Core.Discovery;

/// <summary>
/// A file or folder another person shared with the user (Graph search), and where it is in the owner's drive. Path: '/'-separated from
/// the drive's root, the item's own name included, null while unknown. Name, ParentId and WebUrl as last seen, to notice a move or
/// rename. ReadOnly: the user may only view it. CheckedAt: when Path and ReadOnly were read. Misses: successful searches in a row that
/// didn't return it.
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

/// <summary>The shared list as kept between runs. RunAt: when the last search that worked ended (MinValue: never).</summary>
public sealed record SharedState(DateTimeOffset RunAt, IReadOnlyList<SharedItem> Items);

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

/// <summary>Errors: items whose place couldn't be read this run (kept as they were, or left out when new).</summary>
public sealed record SharedResult(SharedState State, int Hits, string? SearchError, IReadOnlyList<string> Errors);

public sealed record SharedOptions
{
    /// <summary>One search call holds this many hits: a normal user's shares fit in one.</summary>
    public int PageSize { get; init; } = 200;

    /// <summary>Hits read at most per run; more are left out (and the log says so).</summary>
    public int MaxItems { get; init; } = 1000;

    /// <summary>An item's path and rights are read again after this long (or after a move or rename search reports).</summary>
    public TimeSpan CheckMaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Items looked up at the same time.</summary>
    public int Parallel { get; init; } = 4;

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.UtcNow;
}

/// <summary>
/// Reads what other people shared with the user: Graph search for driveItems shared with the user's UPN, paged; items in the user's own
/// OneDrive are left out. New items, items moved or renamed (also a folder above them: their web URL changed) and those checked more than
/// CheckMaxAge ago are looked up for their path in the owner's drive and the user's rights; the rest reuse the last run's answer, so a
/// routine run is one search call. An item search
/// doesn't return goes at the second successful run in a row without it (one short answer doesn't hide anything). Covers sharing with
/// named people only: "anyone" and organization links the user merely opened aren't recorded as shared with the user.
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
        using var slots = new SemaphoreSlim(Math.Max(1, options.Parallel));
        var lookups = found.Values.Select(async hit =>
        {
            var known = before.GetValueOrDefault(hit.Key);
            var current = hit with { Path = known?.Path, ReadOnly = known?.ReadOnly ?? false, CheckedAt = known?.CheckedAt };
            // The web URL holds the whole path: it changes when a folder above the item moves or is renamed
            if (known is { Path: not null, CheckedAt: { } checkedAt } && !recheck && now - checkedAt < options.CheckMaxAge
                && known.Name == hit.Name && (hit.ParentId is null || known.ParentId == hit.ParentId) && (hit.WebUrl is null || known.WebUrl == hit.WebUrl))
            {
                return current;
            }
            await slots.WaitAsync(ct);
            try
            {
                var location = await api.GetLocationAsync(hit.DriveId, hit.ItemId, ct);
                if (location is null)
                {
                    return null; // gone, or no longer shared: search lags behind
                }
                var canEdit = await api.CanEditAsync(location, ct);
                return current with
                {
                    Path = location.Path,
                    Name = location.Name,
                    IsFolder = location.IsFolder,
                    ParentId = location.ParentId ?? hit.ParentId,
                    ReadOnly = canEdit == false,
                    CheckedAt = now,
                };
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                lock (errors)
                {
                    errors.Add($"{hit.Name} ({hit.Key}): {e.Message}");
                }
                return known?.Path is null ? null : current;
            }
            finally
            {
                slots.Release();
            }
        }).ToList();
        foreach (var item in await Task.WhenAll(lookups))
        {
            if (item is not null)
            {
                items.Add(item);
            }
        }
        // Not returned this time: kept once more, so one short search answer hides nothing
        foreach (var missed in before.Values.Where(i => !found.ContainsKey(i.Key) && i.Misses < 1 && i.Path is not null))
        {
            items.Add(missed with { Misses = missed.Misses + 1 });
        }
        items.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return new SharedResult(new SharedState(now, items), hits, null, errors);
    }
}
