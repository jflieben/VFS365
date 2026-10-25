using System.Collections.Concurrent;

namespace Vfs365.Core.Discovery;

/// <summary>Finds the document libraries the user can reach with M365AutoLink's method: SharePoint Search, then site and library checks.</summary>
public sealed class DiscoveryService(ISharePointDiscoveryApi api, DiscoveryOptions options)
{
    public const int SearchPageSize = 500;
    const int MaxSearchPages = 200;

    /// <summary>A site's search hits by what they need: a first read, a read again (metadata expired), or nothing (fresh).</summary>
    sealed record SitePlan(string WebUrl, string? Template)
    {
        public List<DiscoveredLibrary> New { get; } = [];
        public List<DiscoveredLibrary> Expired { get; } = [];
        public List<DiscoveredLibrary> Fresh { get; } = [];
    }

    /// <param name="oneDrive">The user's OneDrive when the caller has just read it (a first start); read here otherwise.</param>
    /// <param name="tenantId">
    /// The signed-in tenant's ID. With it, the last run's state stays when the SharePoint host changed but the tenant didn't (a user
    /// moved to another geo); without it, the host decides.
    /// </param>
    public async Task<DiscoveryResult> RunAsync(DiscoveryState? previous, CancellationToken ct = default, MyDrive? oneDrive = null, string? tenantId = null)
    {
        oneDrive ??= await api.GetMyDriveAsync(ct);
        var tenantRoot = SharePointUrls.TenantRootFromOneDriveUrl(oneDrive.WebUrl);
        if (previous is not null && !SameTenant(previous, tenantRoot, tenantId))
        {
            previous = null;
        }
        tenantId ??= previous?.TenantId;

        var (hits, searchError, searchNote) = await SearchAsync(tenantRoot, ct);
        var sameRules = previous?.RulesVersion == LibraryRules.Version;
        var now = options.Clock();
        var metadataAt = previous?.MetadataAt ?? default;
        var knownStatic = sameRules
            ? previous!.StaticExclusions.Where(e => !LibraryRules.NoLongerStatic(e.Value)).ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Each library's metadata is read again once it is MetadataMaxAge old, so only part of them is looked up per run
        var known = sameRules
            ? previous!.Libraries.DistinctBy(l => l.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        var knownExclusions = sameRules && previous!.Exclusions is { } checkedExclusions
            ? new Dictionary<string, CheckedExclusion>(checkedExclusions, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, CheckedExclusion>(StringComparer.OrdinalIgnoreCase);
        var knownDrives = previous?.Libraries.Where(l => l.DriveId is not null).DistinctBy(l => l.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(l => l.Key, l => l.DriveId!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        DateTimeOffset? CheckedAt(string key) =>
            known.TryGetValue(key, out var entry) ? entry.CheckedAt ?? metadataAt : knownExclusions.TryGetValue(key, out var exclusion) ? exclusion.CheckedAt : null;
        bool Fresh(DateTimeOffset checkedAt) => now - checkedAt is var age && age >= TimeSpan.Zero && age < options.MetadataMaxAge;

        var found = new ConcurrentBag<LibraryEntry>();
        var skipped = new ConcurrentBag<SkippedLibrary>();
        var goneAtOnce = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var errors = new ConcurrentBag<SkippedLibrary>();
        var staticExclusions = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new ConcurrentDictionary<string, CheckedExclusion>(StringComparer.OrdinalIgnoreCase);

        void Exclude(DiscoveredLibrary library, string reason, string? internalName = null)
        {
            skipped.Add(SkippedLibrary.For(library, reason, internalName));
            goneAtOnce[library.Key] = 0;
        }

        // What the last run knew, unchanged
        void Reuse(DiscoveredLibrary library, string? template)
        {
            if (known.TryGetValue(library.Key, out var entry))
            {
                found.Add(entry with { SiteTemplate = template });
            }
            else if (knownExclusions.TryGetValue(library.Key, out var exclusion))
            {
                exclusions[library.Key] = exclusion;
                Exclude(library, exclusion.Reason);
            }
        }

        var plans = new List<SitePlan>();
        foreach (var site in hits.DistinctBy(library => library.Key).GroupBy(library => library.WebUrl, StringComparer.OrdinalIgnoreCase))
        {
            var template = site.Select(library => library.SiteTemplate).FirstOrDefault(t => t is not null);
            var siteRule = Wildcard.MatchesAny(site.Key, options.ExcludedSites) ? "Site excluded by pattern"
                : !Wildcard.MatchesAny(site.Key, options.IncludedSites) ? "Site not included by pattern"
                : options.TemplateRule(template);
            if (siteRule is not null)
            {
                foreach (var library in site)
                {
                    Exclude(library, siteRule);
                }
                continue;
            }

            var plan = new SitePlan(site.Key, template);
            foreach (var library in site)
            {
                if (knownStatic.TryGetValue(library.Key, out var reason))
                {
                    staticExclusions[library.Key] = reason;
                    Exclude(library, reason);
                }
                else if (CheckedAt(library.Key) is { } checkedAt)
                {
                    (Fresh(checkedAt) ? plan.Fresh : plan.Expired).Add(library);
                }
                else
                {
                    plan.New.Add(library);
                }
            }
            plans.Add(plan);
        }

        // Sites with only expired metadata are read again oldest first, a share per run, so a user's libraries don't all come due together
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (options.MetadataMaxAge > TimeSpan.Zero)
        {
            var allowed = Math.Max(options.MinSiteRechecks, (int)Math.Ceiling(plans.Count * options.SiteRecheckShare));
            deferred.UnionWith(plans.Where(p => p.New.Count == 0 && p.Expired.Count > 0)
                .OrderBy(p => p.Expired.Min(l => CheckedAt(l.Key)))
                .Skip(allowed)
                .Select(p => p.WebUrl));
        }

        // ReadOnly and WriteLocked belong to the site collection: one _api/site per collection, shared by its webs (a failed one isn't kept)
        var siteReads = new ConcurrentDictionary<string, Lazy<Task<SiteMetadata>>>(StringComparer.OrdinalIgnoreCase);
        async Task<SiteMetadata> SiteAsync(string siteCollectionUrl, string webUrl, CancellationToken token)
        {
            var read = siteReads.GetOrAdd(siteCollectionUrl, _ => new(() => api.GetSiteAsync(webUrl, token)));
            try
            {
                return await read.Value;
            }
            catch (Exception)
            {
                siteReads.TryRemove(KeyValuePair.Create(siteCollectionUrl, read));
                throw;
            }
        }

        // A site that fails, a timeout included, is that site's error: its libraries keep their last entries and the other sites go on.
        // Failures are told from cancellation by the caller's token, not by the exception's type.
        await Parallel.ForEachAsync(plans, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelSites, CancellationToken = ct }, async (plan, token) =>
        {
            try
            {
                await ReadSiteAsync(plan, token);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                foreach (var library in plan.New.Concat(plan.Expired))
                {
                    errors.Add(SkippedLibrary.For(library, $"Site: {e.Message}"));
                }
                foreach (var library in plan.Fresh)
                {
                    Reuse(library, plan.Template);
                }
            }
        });

        async Task ReadSiteAsync(SitePlan plan, CancellationToken token)
        {
            var due = deferred.Contains(plan.WebUrl) ? plan.New : [.. plan.New, .. plan.Expired];
            if (due.Count == 0)
            {
                foreach (var library in plan.Fresh.Concat(plan.Expired))
                {
                    Reuse(library, plan.Template);
                }
                return;
            }

            SiteMetadata siteMetadata;
            try
            {
                siteMetadata = await SiteAsync(due[0].SiteCollectionUrl, plan.WebUrl, token);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                foreach (var library in due)
                {
                    errors.Add(SkippedLibrary.For(library, $"Site: {e.Message}"));
                }
                foreach (var library in plan.Fresh)
                {
                    Reuse(library, plan.Template);
                }
                return;
            }

            // Two or more libraries: the web's library list answers for all of them in one call (fresh ones are read again with it);
            // a web the user can't list (access to a library, not the site) falls back to one call per library
            var candidates = due.Concat(plan.Fresh).ToList();
            Dictionary<string, ListMetadata>? listed = null;
            if (candidates.Count >= 2)
            {
                try
                {
                    if (await api.GetWebLibrariesAsync(plan.WebUrl, token) is { } libraries)
                    {
                        listed = new Dictionary<string, ListMetadata>(StringComparer.OrdinalIgnoreCase);
                        foreach (var library in libraries)
                        {
                            listed.TryAdd(LibraryKey.Normalize(library.ListId), library.List);
                        }
                    }
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }
            }

            var fresh = plan.Fresh.ToHashSet();
            foreach (var library in candidates)
            {
                var list = listed?.GetValueOrDefault(library.ListId);
                if (list is null && fresh.Contains(library))
                {
                    Reuse(library, plan.Template);
                    continue;
                }
                if (list is null)
                {
                    try
                    {
                        list = await api.GetListAsync(plan.WebUrl, library.ListId, token);
                        if (list is null && !string.Equals(library.SiteCollectionUrl, plan.WebUrl, StringComparison.OrdinalIgnoreCase))
                        {
                            list = await api.GetListAsync(library.SiteCollectionUrl, library.ListId, token);
                        }
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        errors.Add(SkippedLibrary.For(library, $"Library: {e.Message}"));
                        continue;
                    }
                    if (list is null)
                    {
                        errors.Add(SkippedLibrary.For(library, "Library not found"));
                        continue;
                    }
                }

                var checkedAt = CheckedAt(library.Key) is null ? now - FirstReadOffset(library.Key) : now;
                var verdict = LibraryRules.Evaluate(list);
                if (!verdict.Include)
                {
                    Exclude(library, verdict.Reason!, list.InternalName);
                    if (verdict.Static)
                    {
                        staticExclusions[library.Key] = verdict.Reason!;
                    }
                    else
                    {
                        exclusions[library.Key] = new CheckedExclusion(verdict.Reason!, checkedAt);
                    }
                    continue;
                }

                var readOnly = verdict.Access == LibraryAccess.ReadOnly || siteMetadata.ReadOnly || siteMetadata.WriteLocked;
                var title = string.IsNullOrWhiteSpace(list.Title) ? library.ListTitle : list.Title;
                found.Add(new LibraryEntry(library.Key, library.SiteId, library.WebId, library.ListId, plan.WebUrl, library.SiteTitle, title, readOnly, list.ItemCount,
                    knownDrives.GetValueOrDefault(library.Key), list.InternalName, checkedAt, plan.Template));
            }
        }

        // Pinned locations (admin policy, user pins): shown whatever search finds; site patterns don't apply
        var pinned = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var emptyPins = new Dictionary<string, CheckedExclusion>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in options.PinnedLocations.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var knownKeys = previous?.Pinned?.GetValueOrDefault(url);
            if (knownKeys is { Count: > 0 } && knownKeys.All(key => known.TryGetValue(key, out var entry) && Fresh(entry.CheckedAt ?? metadataAt)))
            {
                foreach (var key in knownKeys)
                {
                    found.Add(known[key]);
                }
                pinned[url] = knownKeys;
                continue;
            }
            if (sameRules && previous?.EmptyPins?.GetValueOrDefault(url) is { } empty && Fresh(empty.CheckedAt))
            {
                // Nothing there at the last look: not probed again until the metadata age passes
                skipped.Add(new SkippedLibrary($"pinned|{url}", url, url, empty.Reason));
                emptyPins[url] = empty;
                pinned[url] = [];
                continue;
            }
            try
            {
                var location = await api.ResolveLocationAsync(url, ct);
                if (location is null && knownKeys is not { Count: > 0 })
                {
                    errors.Add(new SkippedLibrary($"pinned|{url}", url, url, "Pinned location: nothing found there"));
                    emptyPins[url] = new CheckedExclusion("Pinned location: nothing found there", now);
                    pinned[url] = [];
                    continue;
                }
                if (location is null)
                {
                    throw new InvalidOperationException("nothing found there"); // it worked before: kept like a failed lookup
                }
                var keys = new List<string>();
                foreach (var library in location.Libraries)
                {
                    var verdict = LibraryRules.Evaluate(library.List);
                    if (!verdict.Include)
                    {
                        continue;
                    }
                    var (siteId, webId, listId) = (LibraryKey.Normalize(location.SiteId), LibraryKey.Normalize(location.WebId), LibraryKey.Normalize(library.ListId));
                    var key = LibraryKey.Of(siteId, webId, listId);
                    var readOnly = verdict.Access == LibraryAccess.ReadOnly || location.Site.ReadOnly || location.Site.WriteLocked;
                    var checkedAt = CheckedAt(key) is null ? now - FirstReadOffset(key) : now;
                    found.Add(new LibraryEntry(key, siteId, webId, listId, location.WebUrl.TrimEnd('/'), location.SiteTitle, library.List.Title, readOnly,
                        library.List.ItemCount, knownDrives.GetValueOrDefault(key), library.List.InternalName, checkedAt));
                    keys.Add(key);
                }
                if (keys.Count == 0)
                {
                    emptyPins[url] = new CheckedExclusion("Pinned location: no document library to show there", now);
                }
                pinned[url] = keys;
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                // Its libraries from the last run stay, like any library that fails a run
                foreach (var key in (IEnumerable<string>?)knownKeys ?? [$"pinned|{url}"])
                {
                    errors.Add(new SkippedLibrary(key, url, url, $"Pinned location: {e.Message}"));
                }
                if (knownKeys is not null)
                {
                    pinned[url] = knownKeys;
                }
            }
        }

        var foundKeys = found.Select(l => l.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reconciliation = DiscoveryReconciler.Reconcile(
            previous?.Libraries ?? [],
            found.DistinctBy(l => l.Key, StringComparer.OrdinalIgnoreCase).ToList(),
            errors.Select(error => error.Key).ToHashSet(),
            searchError is not null,
            options.HidingSafetyRatio,
            goneAtOnce.Keys.Where(key => !foundKeys.Contains(key)).ToHashSet(StringComparer.OrdinalIgnoreCase),
            previous?.Misses);

        var visible = reconciliation.Visible
            .OrderBy(library => library.SiteTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(library => library.LibraryTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
        // A failed search keeps the time of the last run whose search completed, so a restart doesn't take this run as a recent one
        var runAt = searchError is null ? now : previous?.RunAt ?? DateTimeOffset.MinValue;
        var state = new DiscoveryState(runAt, tenantRoot, visible, new Dictionary<string, string>(staticExclusions), LibraryRules.Version, oneDrive,
            visible.Count == 0 ? now : visible.Min(l => l.CheckedAt ?? now), previous?.TenantHint, pinned, tenantId, reconciliation.Misses,
            new Dictionary<string, CheckedExclusion>(exclusions), emptyPins);

        return new DiscoveryResult(
            oneDrive,
            tenantRoot,
            visible,
            skipped.OrderBy(entry => entry.WebUrl, StringComparer.OrdinalIgnoreCase).ToList(),
            errors.OrderBy(entry => entry.WebUrl, StringComparer.OrdinalIgnoreCase).ToList(),
            hits.Count,
            searchError,
            reconciliation.HidingSkippedReason,
            state,
            searchNote);
    }

    /// <summary>By tenant ID when both sides have one, else by SharePoint root.</summary>
    static bool SameTenant(DiscoveryState previous, string tenantRoot, string? tenantId) =>
        tenantId is not null && previous.TenantId is not null
            ? string.Equals(previous.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(previous.TenantRoot, tenantRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>A fixed share of <see cref="DiscoveryOptions.FirstReadJitter"/> per library key (FNV-1a), so runs are repeatable.</summary>
    TimeSpan FirstReadOffset(string key)
    {
        var hash = 2166136261u;
        foreach (var c in key.ToLowerInvariant())
        {
            hash = (hash ^ c) * 16777619u;
        }
        return options.FirstReadJitter * (hash / 4294967296.0);
    }

    /// <summary>
    /// All pages, in DocId order: each page starts after the last DocId of the one before (Microsoft's advice for deep paging; stable
    /// while the index changes). A page without DocIds continues the same query by row number. When the DocIds don't hold up (out of
    /// order, before the last page's, seen before, or from more than one geo), pages after a DocId could skip rows: search goes on by
    /// row number of the plain query, from the start if it had paged by DocId, and Note says so. Error: why the hits are incomplete,
    /// when they are (a failed page included).
    /// </summary>
    async Task<(List<DiscoveredLibrary> Hits, string? Error, string? Note)> SearchAsync(string tenantRoot, CancellationToken ct)
    {
        var hits = new List<DiscoveredLibrary>();
        var seen = new HashSet<long>();
        var geos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var (afterDocId, startRow) = (0L, 0);
        string? note = null;
        for (var pages = 1; ; pages++)
        {
            SearchPage page;
            try
            {
                page = await api.SearchDocumentLibrariesAsync(tenantRoot, afterDocId, startRow, SearchPageSize, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                return (hits, $"Search failed after {hits.Count} rows: {e.Message}", note);
            }

            hits.AddRange(page.Libraries);
            var restart = false;
            if (note is null && DocIdProblem(page, afterDocId, seen, geos) is { } problem)
            {
                // Row numbers of the plain query from now on; it starts over unless this page already was the plain query
                note = $"Search paged by row number: {problem} on page {pages}";
                restart = afterDocId > 0;
            }
            if (page.RowCount < SearchPageSize && !restart)
            {
                return (hits, null, note);
            }
            if (pages == MaxSearchPages)
            {
                return (hits, $"Search stopped after {pages} pages", note);
            }
            if (restart)
            {
                hits.Clear();
                (afterDocId, startRow) = (0, 0);
            }
            else if (note is null && page.LastDocId is { } last && last > afterDocId)
            {
                (afterDocId, startRow) = (last, 0);
            }
            else
            {
                startRow += SearchPageSize;
            }
        }
    }

    /// <summary>Why paging after this page's last DocId could skip rows; null when its DocIds rise, follow <paramref name="afterDocId"/> and are new.</summary>
    static string? DocIdProblem(SearchPage page, long afterDocId, HashSet<long> seen, HashSet<string> geos)
    {
        var previous = afterDocId;
        foreach (var docId in page.DocIds ?? (page.LastDocId is { } last ? [last] : []))
        {
            if (docId is not { } id)
            {
                continue;
            }
            if (!seen.Add(id))
            {
                return $"DocId {id} twice";
            }
            if (id <= previous)
            {
                return $"DocId {id} after {previous}";
            }
            previous = id;
        }
        geos.UnionWith(page.Geos ?? []);
        return geos.Count > 1 ? "results from several geos" : null;
    }
}
