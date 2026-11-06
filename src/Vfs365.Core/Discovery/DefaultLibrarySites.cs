namespace Vfs365.Core.Discovery;

/// <summary>
/// Sites whose folder is their default document library (policy DefaultLibrarySites): Sites\Finance\General instead of
/// Sites\Finance\Documents\General. An entry with a slash is a web URL pattern (as IncludedSites), any other a site template (as
/// IncludedSiteTemplates). A matching site's other libraries never show, and without its default library it has no folder: its paths
/// depend on the policy alone, never on how many libraries a user can reach. Discovery marks the default library
/// (<see cref="LibraryEntry.DefaultLibrary"/>) from Graph's default drive of the site; titles and internal names are localized.
/// </summary>
public sealed class DefaultLibrarySites
{
    public static DefaultLibrarySites None { get; } = new([]);

    readonly string[] urlPatterns;
    readonly string[] templates;

    public DefaultLibrarySites(IEnumerable<string> entries)
    {
        var cleaned = entries.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        urlPatterns = [.. cleaned.Where(e => e.Contains('/'))];
        templates = [.. cleaned.Where(e => !e.Contains('/'))];
    }

    public bool IsEmpty => urlPatterns.Length == 0 && templates.Length == 0;

    /// <summary>Whether the web's folder is its default library; <paramref name="template"/> is its site collection's, as search reports it.</summary>
    public bool Matches(string webUrl, string? template) =>
        Wildcard.MatchesAny(webUrl.TrimEnd('/'), urlPatterns)
        || template is not null && templates.Any(pattern => DiscoveryOptions.TemplateMatches(template, pattern));

    public static bool IsDefault(LibraryEntry library) => library.DefaultLibrary == true;

    /// <summary>A web's template: the first one known among its libraries (pinned ones may lack it).</summary>
    public static string? TemplateOf(IEnumerable<LibraryEntry> web) => web.Select(l => l.SiteTemplate).FirstOrDefault(t => t is not null);

    /// <summary>
    /// The matching webs whose default library discovery has to look up: none of their libraries is marked as the default, and some
    /// weren't checked yet (the policy is new, or a library the user can now reach).
    /// </summary>
    public IEnumerable<IGrouping<string, LibraryEntry>> Unchecked(IEnumerable<LibraryEntry> libraries) =>
        IsEmpty ? [] : libraries.GroupBy(l => l.WebUrl, StringComparer.OrdinalIgnoreCase)
            .Where(web => Matches(web.Key, TemplateOf(web)) && !web.Any(IsDefault) && web.Any(l => l.DefaultLibrary is null));
}
