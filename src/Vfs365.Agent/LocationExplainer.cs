using System.Text.RegularExpressions;
using System.Web;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Agent;

/// <summary>
/// What `vfs365 why` works from: the discovery options as the agent uses them (site patterns and templates from policy, pins from
/// policy and the user), the last discovery's state (discovery.json) and report (discovery-report.json), and the drive's root as
/// Windows shows it (a letter or \\VFS365\&lt;user&gt;). Source: where a setting comes from, for the explanation. SharedWithMe: the policy
/// of that name.
/// </summary>
public sealed record WhyContext(DiscoveryOptions Options, IReadOnlyList<string> PolicyPins, IReadOnlyList<string> UserPins, DriveScope Scope,
    DiscoveryState? State, DiscoveryReport? Report, string DriveRoot, Func<string, string>? Source = null, bool SharedWithMe = true)
{
    DriveNamespace? drive;
    IReadOnlyDictionary<string, string>? sharedNames;

    /// <summary>Where a shared item shows, as Windows shows it; null when the drive has no Shared with me folder.</summary>
    internal string? PathOf(SharedItem item)
    {
        if (Scope != DriveScope.All || !SharedWithMe)
        {
            return null;
        }
        sharedNames ??= DriveNamespace.SharedNames(State?.Shared?.Items ?? []);
        return sharedNames.TryGetValue(item.Key, out var name) ? WindowsPath($@"\{DriveNamespace.SharedFolderName}\{name}") : null;
    }

    /// <summary>The library's folder as Windows shows it, with the names the drive gives the state's libraries.</summary>
    internal string PathOf(LibraryEntry library)
    {
        drive ??= DriveNamespace.Build(State?.OneDrive ?? new MyDrive("", ""), State?.Libraries ?? [], (_, _) => Task.FromResult(""), Scope);
        return drive.VolumePathOfLibrary(library.Key) is { } path ? WindowsPath(path) : "(not on the drive)";
    }

    internal string WindowsPath(string volumePath) => volumePath == @"\" ? DriveRoot + @"\" : DriveRoot + volumePath;
}

/// <param name="Shown">True when the location shows on the drive.</param>
public sealed record WhyAnswer(bool Shown, IReadOnlyList<string> Lines);

/// <summary>
/// Explains why a site or library shows on the drive or not, with DiscoveryService's own rules and reasons: site patterns, site
/// templates, the library rules (LibraryRules), libraries search missed in runs in a row (DiscoveryReconciler), pins, errors, and
/// what search can't find. A browser link (site, library, folder, file or sharing link) or part of a name.
/// </summary>
public static partial class LocationExplainer
{
    const int MaxListed = 30;

    public static WhyAnswer Explain(string query, WhyContext context)
    {
        var lines = new List<string>();
        var state = context.State;
        lines.Add(state is null
            ? "Discovery:  no discovery state in this profile yet (the agent didn't discover libraries here, or 'vfs365 signout' removed it)"
            : $"Discovery:  last run {(state.RunAt == DateTimeOffset.MinValue ? "didn't complete" : $"{state.RunAt.ToLocalTime():yyyy-MM-dd HH:mm}")}, " +
              $"{state.Libraries.Count} libraries shown{(context.Report is { } report ? $", {report.Skipped.Count} skipped, {report.Errors.Count} errors" : "")}");
        if (context.Report?.SearchError is { } searchError)
        {
            lines.Add($"Search:     the last run's search failed ({searchError}); nothing was hidden");
        }
        var shown = Uri.TryCreate(query.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"
            ? ExplainUrl(uri, context, lines)
            : ExplainName(query.Trim(), context, lines);
        return new WhyAnswer(shown, lines);
    }

    /// <summary>A browser link: whose site, which library, and why it shows or not.</summary>
    static bool ExplainUrl(Uri uri, WhyContext context, List<string> lines)
    {
        var host = uri.Host;
        if (host.EndsWith("teams.microsoft.com", StringComparison.OrdinalIgnoreCase) || host.EndsWith("teams.cloud.microsoft", StringComparison.OrdinalIgnoreCase))
        {
            lines.Add("Teams:      a Teams link doesn't name the SharePoint site. In Teams, open the channel's files, choose Open in SharePoint and use that link.");
            return false;
        }
        var state = context.State;
        var tenantHost = Uri.TryCreate(state?.TenantRoot, UriKind.Absolute, out var tenantRoot) ? tenantRoot.Host : null;
        if (uri.Scheme != "https" || tenantHost is not null && !TrustedHosts.IsTrusted(new Uri($"https://{host}/"), tenantHost))
        {
            lines.Add($"Tenant:     {host} is not your organisation's SharePoint{(tenantHost is null ? "" : $" ({tenantHost})")}. VFS365 shows the tenant you sign in to; " +
                "other tenants (also as a guest) are out of scope.");
            return false;
        }
        var path = SharePointPath(uri);
        var target = $"https://{host}{path}";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Shared with the user by name: in the Shared with me folder (Scope All)
        var shared = (state?.Shared?.Items ?? []).Where(i => i.WebUrl is { } web && Under(target, Uri.UnescapeDataString(web).TrimEnd('/'))).ToList();
        var sharedShown = ExplainShared(shared, context, lines);

        // OneDrive: the user's own shows as OneDrive (Scope All or OneDrive); colleagues' only what they shared with the user
        if (host.Split('.')[0].EndsWith("-my", StringComparison.OrdinalIgnoreCase))
        {
            var own = state?.OneDrive?.WebUrl is { } mine && Under(target, PersonalSite(mine));
            if (!own)
            {
                if (shared.Count == 0)
                {
                    lines.Add("OneDrive:   a colleague's OneDrive. Only what was shared with you by name shows, in Shared with me (Scope All); " +
                        "this link isn't in the last list of what was shared with you ('vfs365 shared' reads it now). Open it in the browser.");
                }
                return sharedShown;
            }
            var visible = context.Scope != DriveScope.SharePoint;
            lines.Add(visible
                ? $"Shown:      your OneDrive, at {context.WindowsPath(context.Scope == DriveScope.OneDrive ? @"\" : @"\OneDrive")}"
                : $"Not shown:  your OneDrive. Scope is SharePoint ({Source(context, "Scope")}), which shows only SharePoint sites; Scope All or OneDrive shows the OneDrive.");
            return visible;
        }
        if (context.Scope == DriveScope.OneDrive)
        {
            lines.Add($"Not shown:  Scope is OneDrive ({Source(context, "Scope")}), so no SharePoint site shows; Scope SharePoint or All shows them.");
            return false;
        }

        // The web: the longest web URL discovery knows that the link lies in
        var libraries = state?.Libraries ?? [];
        var webs = libraries.Select(l => l.WebUrl)
            .Concat(context.Report?.Skipped.Select(s => s.WebUrl) ?? []).Concat(context.Report?.Errors.Select(s => s.WebUrl) ?? [])
            .Concat(context.Report?.Hidden.Select(h => h.WebUrl) ?? [])
            .Where(w => Uri.TryCreate(w, UriKind.Absolute, out _) && Under(target, Root(w)))
            .OrderByDescending(w => w.Length).ToList();
        var web = webs.FirstOrDefault();
        var webSegments = web is null ? 0 : new Uri(web).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        var librarySegment = web is not null && segments.Length > webSegments ? segments[webSegments] : null;
        var ruleWeb = web ?? SiteCollection(host, segments);

        var onWeb = web is null ? [] : libraries.Where(l => SameUrl(l.WebUrl, web)).ToList();
        var matched = librarySegment is null ? [] : onWeb.Where(l => Named(librarySegment, l.InternalName, l.LibraryTitle)).ToList();
        var shown = false;
        foreach (var library in librarySegment is null ? onWeb : matched)
        {
            shown = true;
            lines.Add($"Shown:      {library.SiteTitle} / {library.LibraryTitle} at {context.PathOf(library)}{(library.ReadOnly ? ", read-only (the library requires check-out, or the site is read-only or locked)" : "")}");
            if (state?.Misses?.GetValueOrDefault(library.Key) is > 0 and var misses)
            {
                lines.Add($"Missed:     search didn't return it in the last {misses} run(s); it is hidden when {DiscoveryReconciler.MissesToHide} runs in a row miss it");
            }
            if (state?.Pinned?.FirstOrDefault(p => p.Value.Contains(library.Key, StringComparer.OrdinalIgnoreCase)).Key is { } pin)
            {
                lines.Add($"Pinned:     by {PinSource(context, pin)} ({pin})");
            }
        }

        // What the last run left out on this web: all of it for a site link; for a library link that library, and what the site's own
        // rules or errors left out (site patterns, templates, a site that failed: "Site ...", "Site: ...")
        bool Concerns(string entryWeb, string title, string? internalName, string reason) => web is not null && SameUrl(entryWeb, web)
            && (librarySegment is null || reason.StartsWith("Site ", StringComparison.Ordinal) || reason.StartsWith("Site:", StringComparison.Ordinal)
                || Named(librarySegment, internalName, title));
        var report = context.Report;
        var skipped = report?.Skipped.Where(s => Concerns(s.WebUrl, s.Title, s.InternalName, s.Reason)).ToList() ?? [];
        foreach (var entry in skipped.Take(MaxListed))
        {
            lines.Add($"Skipped:    {entry.Title}: {Plain(entry.Reason, context)}");
        }
        foreach (var entry in (report?.Errors.Where(s => Concerns(s.WebUrl, s.Title, s.InternalName, s.Reason)) ?? []).Take(MaxListed))
        {
            lines.Add($"Error:      {entry.Title}: {entry.Reason}. The last run kept what it showed before; the next run tries again.");
        }
        foreach (var entry in (report?.Hidden.Where(h => Concerns(h.WebUrl, h.Title, null, "")) ?? []).Take(MaxListed))
        {
            lines.Add($"Hidden:     {entry.SiteTitle} / {entry.Title} since {entry.Since.ToLocalTime():yyyy-MM-dd}: {entry.Reason}. Search no longer returns it: " +
                "the site may be excluded from search or deleted, or access was removed.");
        }

        // Policy: site patterns and templates apply to search results; pins skip them. A pin counts when the link is in it, or it is in
        // the linked site (not for a link to the root site, which every pin is in)
        var pins = context.PolicyPins.Select(p => (Url: p, By: "policy PinnedLocations")).Concat(context.UserPins.Select(p => (Url: p, By: "you ('vfs365 pin')")))
            .Where(p => Uri.TryCreate(p.Url, UriKind.Absolute, out _) && (Under(target, Root(p.Url)) || segments.Length >= 2 && Under(Root(p.Url), target)))
            .ToList();
        if (!shown && pins.Count == 0)
        {
            if (context.Options.ExcludedSites.FirstOrDefault(p => Wildcard.IsMatch(ruleWeb, p)) is { } excluded)
            {
                lines.Add(excluded.Equals("*/personal/*", StringComparison.OrdinalIgnoreCase)
                    ? "Excluded:   colleagues' OneDrives never show (*/personal/*)."
                    : $"Excluded:   {ruleWeb} matches ExcludedSites pattern '{excluded}' ({Source(context, "ExcludedSites")}); exclusions win. A pinned location shows anyway.");
            }
            else if (!context.Options.IncludedSites.Any(p => Wildcard.IsMatch(ruleWeb, p)))
            {
                lines.Add($"Excluded:   {ruleWeb} matches none of the IncludedSites patterns ({string.Join("; ", context.Options.IncludedSites)}; {Source(context, "IncludedSites")})" +
                    $"{(segments.Length < 2 ? ". The root site is outside the default patterns" : "")}. A pinned location shows anyway.");
            }
            else if (onWeb.FirstOrDefault()?.SiteTemplate is { } template && context.Options.TemplateRule(template) is { } templateRule)
            {
                lines.Add($"Excluded:   {templateRule} (IncludedSiteTemplates, ExcludedSiteTemplates). A pinned location shows anyway.");
            }
        }
        foreach (var (pinUrl, by) in pins)
        {
            var keys = context.State?.Pinned?.GetValueOrDefault(pinUrl);
            var empty = context.State?.EmptyPins?.GetValueOrDefault(pinUrl);
            lines.Add($"Pinned:     {pinUrl} by {by}: " + (keys is null ? "not resolved yet; it shows after the next discovery (Restart in the tray)"
                : empty is not null ? $"{empty.Reason} (checked {empty.CheckedAt.ToLocalTime():yyyy-MM-dd HH:mm}; looked at again after a week)"
                : $"{keys.Count} library(ies)"));
        }

        if (!shown && web is not null && librarySegment is not null && skipped.Count == 0 && onWeb.Count > 0)
        {
            lines.Add($"Library:    '{librarySegment}' is not among the {onWeb.Count} libraries shown for this site, and the last run didn't skip it: it may be a list " +
                "(not a document library), a folder, or a library search doesn't return. Its libraries:");
            lines.AddRange(onWeb.Take(MaxListed).Select(l => $"            {l.LibraryTitle} at {context.PathOf(l)}"));
        }
        if (!shown && web is null && pins.Count == 0 && lines.All(l => !l.StartsWith("Excluded:", StringComparison.Ordinal)))
        {
            lines.Add("Not found:  the last discovery didn't see this site. Search doesn't return sites you can't open, sites excluded from search (NoCrawl, " +
                "'Allow this site to appear in search results' off), Restricted Content Discovery sites, and sites newer than the search index.");
            lines.Add($"Hint:       if it opens in your browser, 'vfs365 pin {new Uri(target).AbsoluteUri}' shows it after Restart; 'vfs365 why <link> --check' tests " +
                "access and search.");
        }
        return shown || sharedShown;
    }

    /// <summary>Shared items a link or name matches: where each shows, or why not. True when one shows.</summary>
    static bool ExplainShared(IReadOnlyList<SharedItem> items, WhyContext context, List<string> lines)
    {
        var shown = false;
        foreach (var item in items.Take(MaxListed))
        {
            var by = item.OwnerName ?? item.OwnerEmail ?? "someone";
            if (context.PathOf(item) is { } path)
            {
                shown = true;
                lines.Add($"Shown:      {(item.IsFolder ? "folder" : "file")} shared with you by {by}, at {path}" +
                    (item.IsFolder ? item.ReadOnly ? ", read-only (you may only view it)" : "" : ", read-only (a shared file at the top of Shared with me)"));
            }
            else
            {
                lines.Add($"Not shown:  {(item.IsFolder ? "folder" : "file")} shared with you by {by} ({item.Name}): " + (context.Scope != DriveScope.All
                    ? $"Shared with me shows only with Scope All (Scope is {context.Scope}, {Source(context, "Scope")})"
                    : $"policy SharedWithMe is off ({Source(context, "SharedWithMe")})"));
            }
        }
        return shown;
    }

    /// <summary>Part of a site or library name, or of its address.</summary>
    static bool ExplainName(string fragment, WhyContext context, List<string> lines)
    {
        if (fragment.Length == 0)
        {
            return false;
        }
        bool Has(params string?[] values) => values.Any(v => v?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true);
        var shown = (context.State?.Libraries ?? []).Where(l => Has(l.SiteTitle, l.LibraryTitle, l.WebUrl, $"{l.SiteTitle} / {l.LibraryTitle}")).ToList();
        var report = context.Report;
        var found = new List<string>();
        found.AddRange(shown.Take(MaxListed).Select(l => $"Shown:      {l.SiteTitle} / {l.LibraryTitle} at {context.PathOf(l)} ({l.WebUrl})"));
        found.AddRange((report?.Skipped.Where(s => Has(s.Title, s.WebUrl)) ?? []).Take(MaxListed).Select(s => $"Skipped:    {s.WebUrl} / {s.Title}: {Plain(s.Reason, context)}"));
        found.AddRange((report?.Errors.Where(s => Has(s.Title, s.WebUrl)) ?? []).Take(MaxListed).Select(s => $"Error:      {s.WebUrl} / {s.Title}: {s.Reason}"));
        found.AddRange((report?.Hidden.Where(h => Has(h.Title, h.SiteTitle, h.WebUrl)) ?? []).Take(MaxListed).Select(h => $"Hidden:     {h.SiteTitle} / {h.Title} ({h.WebUrl}): {h.Reason}"));
        found.AddRange(context.PolicyPins.Where(p => Has(p)).Select(p => $"Pinned:     {p} by policy PinnedLocations"));
        found.AddRange(context.UserPins.Where(p => Has(p)).Select(p => $"Pinned:     {p} by you ('vfs365 pin')"));
        var shared = (context.State?.Shared?.Items ?? []).Where(i => Has(i.Name, i.OwnerName, i.OwnerEmail)).ToList();
        var sharedShown = ExplainShared(shared, context, found);
        if (found.Count == 0)
        {
            lines.Add($"Not found:  nothing named like '{fragment}' in the last discovery. Search doesn't return every site (see 'vfs365 why <link>'); " +
                "give the site's or library's browser link to see why.");
            return false;
        }
        lines.AddRange(found);
        return shown.Count > 0 || sharedShown;
    }

    /// <summary>A discovery reason in plain words, with the setting behind it.</summary>
    static string Plain(string reason, WhyContext context) => reason switch
    {
        "Site excluded by pattern" => $"the site matches an ExcludedSites pattern ({Source(context, "ExcludedSites")}); a pinned location shows anyway",
        "Site not included by pattern" => $"the site matches none of the IncludedSites patterns ({Source(context, "IncludedSites")}); a pinned location shows anyway",
        LibraryRules.HiddenReason => "the library is hidden in SharePoint (list setting); checked again weekly",
        "System or catalog library" => "a system library (site assets, pages, style library and the like) or a catalog",
        "Site owner excluded it from offline clients" => "the site owner turned off 'Allow items from this list to be downloaded to offline clients'; checked again weekly",
        _ when reason.StartsWith("Site template ", StringComparison.Ordinal) => $"{reason} (IncludedSiteTemplates, ExcludedSiteTemplates); a pinned location shows anyway",
        _ when reason.StartsWith("Not a standard document library", StringComparison.Ordinal) => $"{reason}: only standard document libraries show",
        _ => reason,
    };

    /// <summary>
    /// `--check`: a few live calls. Whether the user can open the location and which of its libraries the library rules show, then
    /// whether search returns the site's libraries (what discovery relies on; pinned locations don't need it).
    /// </summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(string url, Func<string, CancellationToken, Task<PinnedLocation?>> resolve,
        Func<string, CancellationToken, Task<IReadOnlyList<DiscoveredLibrary>>> searchSite, CancellationToken ct)
    {
        var lines = new List<string>();
        PinnedLocation? location;
        try
        {
            location = await resolve(url, ct);
        }
        catch (RemoteException e) when (e.Error == RemoteError.AccessDenied)
        {
            lines.Add($"Access:     no access, SharePoint refused it ({e.Message.Split('\n')[0]}); the site owner can give access");
            return lines;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            lines.Add($"Access:     the check failed: {e.Message.Split('\n')[0]}");
            return lines;
        }
        if (location is null)
        {
            lines.Add("Access:     SharePoint has no site or library at this address for you (not found)");
            return lines;
        }
        lines.Add($"Access:     you can open {location.WebUrl} (\"{location.SiteTitle}\"){(location.Site.ReadOnly || location.Site.WriteLocked ? ", read-only or locked" : "")}");
        foreach (var library in location.Libraries.Take(MaxListed))
        {
            var verdict = LibraryRules.Evaluate(library.List);
            lines.Add($"            {library.List.Title}: {(verdict.Include ? verdict.Access == LibraryAccess.ReadOnly ? "shows read-only (requires check-out)" : "shows" : $"left out, {verdict.Reason}")}");
        }
        try
        {
            var hits = (await searchSite(location.WebUrl, ct)).Where(h => SameUrl(h.WebUrl, location.WebUrl)).ToList();
            lines.Add(hits.Count > 0
                ? $"Search:     returns {hits.Count} of its libraries, so discovery finds it ({string.Join(", ", hits.Select(h => h.ListTitle).Take(10))})"
                : $"Search:     returns none of its libraries: the site is excluded from search (NoCrawl), has Restricted Content Discovery, or is newer than the index. " +
                  $"'vfs365 pin {url}' shows it anyway.");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            lines.Add($"Search:     the check failed: {e.Message.Split('\n')[0]}");
        }
        return lines;
    }

    /// <summary>
    /// The SharePoint path a browser link stands for, decoded and without a trailing slash: sharing links (/:w:/r/sites/x/..., /:w:/s/x/...
    /// for /sites/x, t for /teams/x), view and folder links (?id= or RootFolder=), and pages (_layouts, Forms, SitePages) as the web or
    /// library they belong to.
    /// </summary>
    public static string SharePointPath(Uri uri)
    {
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        if (SharingLink().Match(path) is { Success: true } share)
        {
            var rest = share.Groups[2].Value;
            var first = rest.Split('/')[0];
            path = share.Groups[1].Value.ToLowerInvariant() switch
            {
                "s" => $"/sites/{first}",
                "t" => $"/teams/{first}",
                _ => $"/{rest}",
            };
        }
        var query = HttpUtility.ParseQueryString(uri.Query);
        if ((query["id"] ?? query["RootFolder"]) is { } folder && folder.StartsWith('/'))
        {
            path = folder;
        }
        foreach (var page in new[] { "/_layouts/", "/Forms/", "/SitePages/", "/_api/", "/_vti_bin/" })
        {
            if (path.IndexOf(page, StringComparison.OrdinalIgnoreCase) is var at and >= 0)
            {
                path = path[..at];
            }
        }
        return path.TrimEnd('/');
    }

    [GeneratedRegex(@"^/:[a-z]:/([a-z])/(.*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SharingLink();

    /// <summary>A site collection's URL from a path: /sites/x, /teams/x or /portals/x, else the root site.</summary>
    static string SiteCollection(string host, string[] segments) =>
        segments.Length >= 2 && segments[0].ToLowerInvariant() is "sites" or "teams" or "portals" ? $"https://{host}/{segments[0]}/{segments[1]}" : $"https://{host}";

    /// <summary>host + decoded path, for comparing URLs.</summary>
    static string Root(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"https://{uri.Host}{Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/')}" : url;

    static bool SameUrl(string a, string b) => Root(a).Equals(Root(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="url"/> is <paramref name="root"/> or below it.</summary>
    static bool Under(string url, string root) =>
        url.Equals(root, StringComparison.OrdinalIgnoreCase) || url.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The URL segment of a library is its internal name (EntityTypeName, decoded) or its title, compared without spaces and
    /// underscores: SharePoint encodes the folder name into the internal name ("Gedeelde documenten" can come back with two spaces).
    /// </summary>
    static bool Named(string segment, string? internalName, string title) =>
        new[] { internalName, title }.Any(name => name is not null && Squash(name).Equals(Squash(segment), StringComparison.OrdinalIgnoreCase));

    static string Squash(string name) => string.Concat(name.Where(c => !char.IsWhiteSpace(c) && c != '_'));

    /// <summary>The personal site of a OneDrive URL (https://contoso-my.sharepoint.com/personal/ann_contoso_com).</summary>
    static string PersonalSite(string oneDriveUrl)
    {
        var root = Root(oneDriveUrl);
        var uri = new Uri(root);
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? $"https://{uri.Host}/{segments[0]}/{segments[1]}" : root;
    }

    static string Source(WhyContext context, string setting) => context.Source?.Invoke(setting) ?? "default";

    static string PinSource(WhyContext context, string pin) =>
        context.PolicyPins.Contains(pin, StringComparer.OrdinalIgnoreCase) ? "policy PinnedLocations" : "you ('vfs365 pin')";
}
