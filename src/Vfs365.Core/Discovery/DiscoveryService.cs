using System.Collections.Concurrent;

namespace Vfs365.Core.Discovery;

/// <summary>Finds the document libraries the user can reach with M365AutoLink's method: SharePoint Search, then site and library checks.</summary>
public sealed class DiscoveryService(ISharePointDiscoveryApi api, DiscoveryOptions options)
{
    public const int SearchPageSize = 500;

    public async Task<DiscoveryResult> RunAsync(DiscoveryState? previous, CancellationToken ct = default)
    {
        var oneDrive = await api.GetMyDriveAsync(ct);
        var tenantRoot = SharePointUrls.TenantRootFromOneDriveUrl(oneDrive.WebUrl);
        if (previous is not null && !string.Equals(previous.TenantRoot, tenantRoot, StringComparison.OrdinalIgnoreCase))
        {
            previous = null;
        }

        var (hits, searchError) = await SearchAsync(tenantRoot, ct);
        var sameRules = previous?.RulesVersion == LibraryRules.Version;
        var knownStatic = sameRules ? previous!.StaticExclusions : new Dictionary<string, string>();
        var now = options.Clock();
        // Each library's metadata is read again once it is MetadataMaxAge old, so only part of them is looked up per run
        var reuse = sameRules
            ? previous!.Libraries.Where(l => now - (l.CheckedAt ?? previous.MetadataAt) is var age && age >= TimeSpan.Zero && age < options.MetadataMaxAge)
                .ToDictionary(l => l.Key, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, LibraryEntry>(StringComparer.OrdinalIgnoreCase);
        var knownDrives = previous?.Libraries.Where(l => l.DriveId is not null).ToDictionary(l => l.Key, l => l.DriveId!, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var found = new ConcurrentBag<LibraryEntry>();
        var skipped = new ConcurrentBag<SkippedLibrary>();
        var hiddenByPolicy = new ConcurrentBag<string>();
        var errors = new ConcurrentBag<SkippedLibrary>();
        var staticExclusions = new ConcurrentDictionary<string, string>();

        var sites = hits.DistinctBy(library => library.Key).GroupBy(library => library.WebUrl, StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(sites, new ParallelOptions { MaxDegreeOfParallelism = options.MaxParallelSites, CancellationToken = ct }, async (site, token) =>
        {
            var template = site.Select(library => library.SiteTemplate).FirstOrDefault(t => t is not null);
            var siteRule = Wildcard.MatchesAny(site.Key, options.ExcludedSites) ? "Site excluded by pattern"
                : !Wildcard.MatchesAny(site.Key, options.IncludedSites) ? "Site not included by pattern"
                : options.TemplateRule(template);
            if (siteRule is not null)
            {
                foreach (var library in site)
                {
                    skipped.Add(SkippedLibrary.For(library, siteRule));
                    hiddenByPolicy.Add(library.Key);
                }
                return;
            }

            var pending = new List<DiscoveredLibrary>();
            foreach (var library in site)
            {
                if (knownStatic.TryGetValue(library.Key, out var reason))
                {
                    staticExclusions[library.Key] = reason;
                    skipped.Add(SkippedLibrary.For(library, reason));
                }
                else if (reuse.TryGetValue(library.Key, out var known))
                {
                    found.Add(known with { SiteTemplate = template });
                }
                else
                {
                    pending.Add(library);
                }
            }
            if (pending.Count == 0)
            {
                return;
            }

            SiteMetadata siteMetadata;
            try
            {
                siteMetadata = await api.GetSiteAsync(site.Key, token);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                foreach (var library in pending)
                {
                    errors.Add(SkippedLibrary.For(library, $"Site: {e.Message}"));
                }
                return;
            }

            foreach (var library in pending)
            {
                ListMetadata? list;
                try
                {
                    list = await api.GetListAsync(site.Key, library.ListId, token);
                    if (list is null && !string.Equals(library.SiteCollectionUrl, site.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        list = await api.GetListAsync(library.SiteCollectionUrl, library.ListId, token);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    errors.Add(SkippedLibrary.For(library, $"Library: {e.Message}"));
                    continue;
                }
                if (list is null)
                {
                    errors.Add(SkippedLibrary.For(library, "Library not found"));
                    continue;
                }

                var verdict = LibraryRules.Evaluate(list);
                if (!verdict.Include)
                {
                    skipped.Add(SkippedLibrary.For(library, verdict.Reason!, list.InternalName));
                    if (verdict.Static)
                    {
                        staticExclusions[library.Key] = verdict.Reason!;
                    }
                    continue;
                }

                var readOnly = verdict.Access == LibraryAccess.ReadOnly || siteMetadata.ReadOnly || siteMetadata.WriteLocked;
                var title = string.IsNullOrWhiteSpace(list.Title) ? library.ListTitle : list.Title;
                found.Add(new LibraryEntry(library.Key, library.SiteId, library.WebId, library.ListId, site.Key, library.SiteTitle, title, readOnly, list.ItemCount,
                    knownDrives.GetValueOrDefault(library.Key), list.InternalName, now, template));
            }
        });

        // Pinned locations (admin policy, user pins): shown whatever search finds; site patterns don't apply
        var pinned = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var url in options.PinnedLocations.Select(u => u.Trim()).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var known = previous?.Pinned?.GetValueOrDefault(url);
            if (known is not null && known.Count > 0 && known.All(reuse.ContainsKey))
            {
                foreach (var key in known)
                {
                    found.Add(reuse[key]);
                }
                pinned[url] = known;
                continue;
            }
            try
            {
                var location = await api.ResolveLocationAsync(url, ct) ?? throw new InvalidOperationException("nothing found there");
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
                    found.Add(new LibraryEntry(key, siteId, webId, listId, location.WebUrl.TrimEnd('/'), location.SiteTitle, library.List.Title, readOnly,
                        library.List.ItemCount, knownDrives.GetValueOrDefault(key), library.List.InternalName, now));
                    keys.Add(key);
                }
                pinned[url] = keys;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Its libraries from the last run stay, like any library that fails a run
                foreach (var key in (IEnumerable<string>?)known ?? [$"pinned|{url}"])
                {
                    errors.Add(new SkippedLibrary(key, url, url, $"Pinned location: {e.Message}"));
                }
                if (known is not null)
                {
                    pinned[url] = known;
                }
            }
        }

        var reconciliation = DiscoveryReconciler.Reconcile(
            previous?.Libraries ?? [],
            found.DistinctBy(l => l.Key, StringComparer.OrdinalIgnoreCase).ToList(),
            errors.Select(error => error.Key).ToHashSet(),
            searchError is not null,
            options.HidingSafetyRatio,
            hiddenByPolicy.Except(found.Select(l => l.Key)).ToHashSet());

        var libraries = reconciliation.Visible
            .OrderBy(library => library.SiteTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(library => library.LibraryTitle, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var state = new DiscoveryState(now, tenantRoot, libraries, new Dictionary<string, string>(staticExclusions), LibraryRules.Version, oneDrive,
            libraries.Count == 0 ? now : libraries.Min(l => l.CheckedAt ?? now), previous?.TenantHint, pinned);

        return new DiscoveryResult(
            oneDrive,
            tenantRoot,
            libraries,
            skipped.OrderBy(entry => entry.WebUrl, StringComparer.OrdinalIgnoreCase).ToList(),
            errors.OrderBy(entry => entry.WebUrl, StringComparer.OrdinalIgnoreCase).ToList(),
            hits.Count,
            searchError,
            reconciliation.HidingSkippedReason,
            state);
    }

    async Task<(List<DiscoveredLibrary> Hits, string? Error)> SearchAsync(string tenantRoot, CancellationToken ct)
    {
        var hits = new List<DiscoveredLibrary>();
        for (var startRow = 0; ; startRow += SearchPageSize)
        {
            SearchPage page;
            try
            {
                page = await api.SearchDocumentLibrariesAsync(tenantRoot, startRow, SearchPageSize, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return (hits, $"Search failed at row {startRow}: {e.Message}");
            }

            hits.AddRange(page.Libraries);
            if (page.RowCount < SearchPageSize)
            {
                return (hits, null);
            }
        }
    }
}
