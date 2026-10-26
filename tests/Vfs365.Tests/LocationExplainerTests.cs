using Vfs365.Agent;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Tests;

/// <summary>`vfs365 why`: why a site or library shows or not, from the stored discovery state and report and the policy.</summary>
public sealed class LocationExplainerTests
{
    const string Root = "https://contoso.sharepoint.com";
    static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);

    static LibraryEntry Library(string site, string title, string listId, string? internalName = null, bool readOnly = false, string? template = "GROUP") =>
        new(LibraryKey.Of($"00000000-0000-0000-0000-{site.Length:x12}", $"10000000-0000-0000-0000-{site.Length:x12}", listId), "s", "w", listId,
            $"{Root}/sites/{site}", site, title, readOnly, 10, InternalName: internalName, SiteTemplate: template);

    static readonly LibraryEntry Documents = Library("Finance", "Documents", "20000000-0000-0000-0000-000000000001", "Shared Documents");
    static readonly LibraryEntry Archive = Library("Finance", "Archive", "20000000-0000-0000-0000-000000000002", "Archive", readOnly: true);
    static readonly LibraryEntry Projects = Library("Projects", "Documenten", "20000000-0000-0000-0000-000000000003", "Gedeelde  documenten");

    static DiscoveryState State() => new(T0, Root, [Documents, Archive, Projects], new Dictionary<string, string>(),
        OneDrive: new MyDrive("b!me", "https://contoso-my.sharepoint.com/personal/ann_contoso_com/Documents"),
        Misses: new Dictionary<string, int> { [Archive.Key] = 1 },
        Pinned: new Dictionary<string, IReadOnlyList<string>> { [$"{Root}/sites/Projects"] = [Projects.Key], [$"{Root}/sites/Empty"] = [] },
        EmptyPins: new Dictionary<string, CheckedExclusion> { [$"{Root}/sites/Empty"] = new("Pinned location: nothing found there", T0) });

    static DiscoveryReport Report() => new(T0, 5, null, null, null,
        [
            new SkippedLibrary("k1", $"{Root}/sites/Finance", "Site Assets", "System or catalog library", "SiteAssets"),
            new SkippedLibrary("k2", $"{Root}/sites/HR", "Documents", "Site excluded by pattern"),
            new SkippedLibrary("k3", $"{Root}/sites/Sales-Private", "Documents", "Site template TEAMCHANNEL excluded"),
            new SkippedLibrary("k4", $"{Root}/sites/Finance", "Hidden stuff", LibraryRules.HiddenReason, "HiddenStuff"),
        ],
        [new SkippedLibrary("k5", $"{Root}/sites/Legal", "Contracts", "Site: 503 ServiceUnavailable from https://x: busy")],
        [new HiddenLibrary("k6", $"{Root}/sites/Old", "Old", "Documents", "Not found by search (or a pinned location) in 2 discovery runs in a row, so it was hidden", T0)]);

    static WhyContext Context(DriveScope scope = DriveScope.SharePoint, IReadOnlyList<string>? userPins = null, bool state = true) => new(
        new DiscoveryOptions { ExcludedSites = ["*/sites/HR*", "*/personal/*"] }, [$"{Root}/sites/Projects", $"{Root}/sites/Empty"], userPins ?? [], scope,
        state ? State() : null, state ? Report() : null, "M:", name => name == "ExcludedSites" ? "computer policy" : "default");

    static WhyAnswer Why(string query, WhyContext? context = null) => LocationExplainer.Explain(query, context ?? Context());

    static string Text(WhyAnswer answer) => string.Join("\n", answer.Lines);

    [Fact]
    public void A_library_link_says_where_it_shows()
    {
        var answer = Why($"{Root}/sites/Finance/Shared%20Documents/Forms/AllItems.aspx");
        Assert.True(answer.Shown);
        Assert.Contains(@"Shown:      Finance / Documents at M:\Finance\Documents", Text(answer));
        Assert.DoesNotContain("Archive", Text(answer));
    }

    [Theory]
    [InlineData(Root + "/sites/Finance/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2FFinance%2FShared%20Documents%2FQ3&viewid=1")]
    [InlineData(Root + "/sites/Finance/Shared%20Documents/Q3/Budget.xlsx")]
    [InlineData(Root + "/:x:/r/sites/Finance/Shared%20Documents/Q3/Budget.xlsx?d=w1&csf=1")]
    public void Folder_file_and_sharing_links_name_their_library(string link) =>
        Assert.Contains(@"Finance / Documents at M:\Finance\Documents", Text(Why(link)));

    [Theory]
    [InlineData(Root + "/sites/Finance")]
    [InlineData(Root + "/sites/Finance/SitePages/Home.aspx")]
    [InlineData(Root + "/:w:/r/sites/Finance/_layouts/15/Doc.aspx?sourcedoc=%7B1%7D&file=a.docx")]
    [InlineData(Root + "/:x:/s/Finance/EaBcDeFgHiJ?e=abc")]
    public void A_site_link_lists_its_libraries_and_what_was_left_out(string link)
    {
        var answer = Why(link);
        Assert.True(answer.Shown);
        var text = Text(answer);
        Assert.Contains(@"Finance / Documents at M:\Finance\Documents", text);
        Assert.Contains("Finance / Archive at", text);
        Assert.Contains("read-only", text);
        Assert.Contains("Missed:     search didn't return it in the last 1 run(s)", text);
        Assert.Contains("Skipped:    Site Assets: a system library", text);
        Assert.Contains("Skipped:    Hidden stuff: the library is hidden in SharePoint", text);
    }

    [Fact]
    public void A_skipped_library_says_which_rule()
    {
        var answer = Why($"{Root}/sites/Finance/SiteAssets/Forms/AllItems.aspx");
        Assert.False(answer.Shown);
        Assert.Contains("Skipped:    Site Assets: a system library", Text(answer));
        Assert.DoesNotContain("Hidden stuff", Text(answer));
    }

    [Fact]
    public void The_internal_name_matches_without_spaces_and_underscores() =>
        Assert.Contains(@"Projects / Documenten at M:\Projects\Documenten", Text(Why($"{Root}/sites/Projects/Gedeelde%20documenten/Forms/AllItems.aspx")));

    [Fact]
    public void A_site_excluded_by_policy_names_the_pattern_and_its_source()
    {
        var text = Text(Why($"{Root}/sites/HR/Shared%20Documents"));
        Assert.Contains("matches ExcludedSites pattern '*/sites/HR*' (computer policy)", text);
        Assert.Contains("Skipped:    Documents: the site matches an ExcludedSites pattern", text);
    }

    [Fact]
    public void Site_templates_and_errors_are_explained()
    {
        Assert.Contains("Site template TEAMCHANNEL excluded (IncludedSiteTemplates, ExcludedSiteTemplates)", Text(Why($"{Root}/sites/Sales-Private")));
        var legal = Text(Why($"{Root}/sites/Legal/Contracts"));
        Assert.Contains("Error:      Contracts: Site: 503 ServiceUnavailable", legal);
        Assert.Contains("the next run tries again", legal);
    }

    [Fact]
    public void A_library_search_missed_twice_is_hidden()
    {
        var answer = Why($"{Root}/sites/Old");
        Assert.False(answer.Shown);
        Assert.Contains("Hidden:     Old / Documents since 2026-10-25: Not found by search", Text(answer));
    }

    [Fact]
    public void The_root_site_is_outside_the_default_patterns()
    {
        var text = Text(Why($"{Root}/"));
        Assert.Contains("matches none of the IncludedSites patterns (*/sites/*; */teams/*; default). The root site is outside the default patterns", text);
    }

    [Fact]
    public void A_site_discovery_never_saw_suggests_a_pin()
    {
        var answer = Why($"{Root}/sites/Unknown%20Site/Shared%20Documents");
        Assert.False(answer.Shown);
        var text = Text(answer);
        Assert.Contains("Not found:  the last discovery didn't see this site", text);
        Assert.Contains("NoCrawl", text);
        Assert.Contains($"'vfs365 pin {Root}/sites/Unknown%20Site/Shared%20Documents'", text);
    }

    [Fact]
    public void Pins_say_what_they_found()
    {
        Assert.Contains("Pinned:     by policy PinnedLocations", Text(Why($"{Root}/sites/Projects")));
        Assert.Contains("Pinned location: nothing found there (checked", Text(Why($"{Root}/sites/Empty")));
        var mine = Text(Why($"{Root}/sites/Secret", Context(userPins: [$"{Root}/sites/Secret"])));
        Assert.Contains("by you ('vfs365 pin'): not resolved yet", mine);
        Assert.DoesNotContain("Not found:", mine);
    }

    [Fact]
    public void Onedrives_depend_on_scope_and_owner()
    {
        Assert.Contains("a colleague's OneDrive", Text(Why("https://contoso-my.sharepoint.com/personal/bob_contoso_com/Documents/x.docx")));
        var own = "https://contoso-my.sharepoint.com/personal/ann_contoso_com/_layouts/15/onedrive.aspx";
        Assert.False(Why(own).Shown);
        Assert.Contains("Scope is SharePoint", Text(Why(own)));
        var all = Why(own, Context(DriveScope.All));
        Assert.True(all.Shown);
        Assert.Contains(@"your OneDrive, at M:\OneDrive", Text(all));
    }

    [Fact]
    public void Items_shared_with_the_user_show_in_shared_with_me_with_scope_all()
    {
        SharedItem[] shared =
        [
            new("b!bob", "01A", "Plan.docx", false, "Docs/Plan.docx", "Bob", "bob@contoso.com", WebUrl: "https://contoso-my.sharepoint.com/personal/bob_contoso_com/Documents/Docs/Plan.docx"),
            new("b!bob", "01F", "Q3", true, "Docs/Q3", "Bob", "bob@contoso.com", ReadOnly: true, WebUrl: "https://contoso-my.sharepoint.com/personal/bob_contoso_com/Documents/Docs/Q3"),
        ];
        WhyContext With(DriveScope scope, bool on = true) => Context(scope) with { State = State() with { Shared = new SharedState(T0, shared) }, SharedWithMe = on };

        var file = Why("https://contoso-my.sharepoint.com/personal/bob_contoso_com/Documents/Docs/Plan.docx", With(DriveScope.All));
        Assert.True(file.Shown);
        Assert.Contains(@"file shared with you by Bob, at M:\Shared with me\Plan.docx, read-only", Text(file));
        var inFolder = Why("https://contoso-my.sharepoint.com/personal/bob_contoso_com/Documents/Docs/Q3/Budget.xlsx", With(DriveScope.All));
        Assert.Contains(@"folder shared with you by Bob, at M:\Shared with me\Q3, read-only (you may only view it)", Text(inFolder));
        Assert.Contains(@"M:\Shared with me\Q3", Text(Why("q3", With(DriveScope.All))));

        Assert.False(Why("https://contoso-my.sharepoint.com/personal/bob_contoso_com/Documents/Docs/Plan.docx", With(DriveScope.SharePoint)).Shown);
        Assert.Contains("shows only with Scope All", Text(Why("Plan.docx", With(DriveScope.SharePoint))));
        Assert.Contains("policy SharedWithMe is off", Text(Why("Plan.docx", With(DriveScope.All, on: false))));
        Assert.Contains("isn't in the last list", Text(Why("https://contoso-my.sharepoint.com/personal/bob_contoso_com/Documents/Other.docx", With(DriveScope.All))));
    }

    [Fact]
    public void Other_tenants_teams_links_and_onedrive_scope_are_explained()
    {
        Assert.Contains("fabrikam.sharepoint.com is not your organisation's SharePoint (contoso.sharepoint.com)", Text(Why("https://fabrikam.sharepoint.com/sites/x")));
        Assert.Contains("Open in SharePoint", Text(Why("https://teams.microsoft.com/l/channel/19%3a1/General?groupId=1")));
        Assert.Contains("Scope is OneDrive", Text(Why($"{Root}/sites/Finance", Context(DriveScope.OneDrive))));
        Assert.Contains(@"M:\Sites\Finance\Documents", Text(Why($"{Root}/sites/Finance/Shared%20Documents", Context(DriveScope.All))));
    }

    [Fact]
    public void A_list_on_a_shown_site_is_named_as_not_a_library()
    {
        var text = Text(Why($"{Root}/sites/Finance/Lists/Tasks/AllItems.aspx"));
        Assert.Contains("'Lists' is not among the 2 libraries shown for this site", text);
        Assert.Contains("Documents at M:", text);
    }

    [Fact]
    public void Without_discovery_state_the_policy_still_answers()
    {
        var text = Text(Why($"{Root}/sites/HR", Context(state: false)));
        Assert.Contains("no discovery state in this profile yet", text);
        Assert.Contains("ExcludedSites pattern '*/sites/HR*'", text);
    }

    [Fact]
    public void Part_of_a_name_lists_what_matches()
    {
        var answer = Why("fin");
        Assert.True(answer.Shown);
        var text = Text(answer);
        Assert.Contains($@"Shown:      Finance / Documents at M:\Finance\Documents ({Root}/sites/Finance)", text);
        Assert.Contains("Skipped:    https://contoso.sharepoint.com/sites/Finance / Site Assets", text);
        Assert.Contains("Error:      ", Text(Why("contracts")));
        Assert.Contains("Hidden:     Old / Documents", Text(Why("old")));
        var none = Why("zzz");
        Assert.False(none.Shown);
        Assert.Contains("nothing named like 'zzz'", Text(none));
    }

    [Theory]
    [InlineData(Root + "/:w:/r/sites/Finance/Shared%20Documents/a.docx?d=1", "/sites/Finance/Shared Documents/a.docx")]
    [InlineData(Root + "/:x:/s/Finance/EaBcDe?e=1", "/sites/Finance")]
    [InlineData(Root + "/:f:/t/Sales/EsXyZ", "/teams/Sales")]
    [InlineData(Root + "/sites/Finance/_layouts/15/Doc.aspx?sourcedoc=%7B1%7D", "/sites/Finance")]
    [InlineData(Root + "/sites/Finance/Shared%20Documents/Forms/AllItems.aspx?id=%2Fsites%2FFinance%2FShared%20Documents%2FQ3", "/sites/Finance/Shared Documents/Q3")]
    [InlineData(Root + "/sites/Finance/Lib/Forms/AllItems.aspx?RootFolder=%2Fsites%2FFinance%2FLib%2FA", "/sites/Finance/Lib/A")]
    [InlineData(Root + "/", "")]
    public void Browser_links_become_sharepoint_paths(string link, string path) => Assert.Equal(path, LocationExplainer.SharePointPath(new Uri(link)));

    static PinnedLocation Location(params PinnedLibrary[] libraries) =>
        new($"{Root}/sites/Secret", "site", "web", "Secret", new SiteMetadata(false, false), libraries);

    static PinnedLibrary List(string title, bool hidden = false) => new(Guid.NewGuid().ToString(), new ListMetadata(title, hidden, 101, false, false, null, false, false, 3));

    [Fact]
    public async Task Check_says_whether_the_user_can_open_it_and_search_returns_it()
    {
        var hits = new List<DiscoveredLibrary> { new("s", "w", "l", $"{Root}/sites/Secret", $"{Root}/sites/Secret", "Secret", "Documents") };
        var lines = string.Join("\n", await LocationExplainer.CheckAsync($"{Root}/sites/Secret", (_, _) => Task.FromResult<PinnedLocation?>(Location(List("Documents"), List("Hidden", hidden: true))),
            (_, _) => Task.FromResult<IReadOnlyList<DiscoveredLibrary>>(hits), default));
        Assert.Contains($"Access:     you can open {Root}/sites/Secret", lines);
        Assert.Contains("Documents: shows", lines);
        Assert.Contains("Hidden: left out, Hidden library", lines);
        Assert.Contains("Search:     returns 1 of its libraries", lines);

        var unsearchable = string.Join("\n", await LocationExplainer.CheckAsync($"{Root}/sites/Secret", (_, _) => Task.FromResult<PinnedLocation?>(Location(List("Documents"))),
            (_, _) => Task.FromResult<IReadOnlyList<DiscoveredLibrary>>([]), default));
        Assert.Contains("returns none of its libraries", unsearchable);
        Assert.Contains("vfs365 pin", unsearchable);
    }

    [Fact]
    public async Task Check_names_missing_access_and_missing_sites()
    {
        static Task<IReadOnlyList<DiscoveredLibrary>> NoSearch(string web, CancellationToken ct) => throw new InvalidOperationException("not called");
        var denied = await LocationExplainer.CheckAsync($"{Root}/sites/Secret",
            (_, _) => throw new RemoteException(RemoteError.AccessDenied, "403 Forbidden from https://x: accessDenied"), NoSearch, default);
        Assert.StartsWith("Access:     no access", Assert.Single(denied));
        var missing = await LocationExplainer.CheckAsync($"{Root}/sites/Nope", (_, _) => Task.FromResult<PinnedLocation?>(null), NoSearch, default);
        Assert.Contains("no site or library at this address", Assert.Single(missing));
    }

    [Fact]
    public void The_check_searches_below_the_web_only()
    {
        var uri = Uri.UnescapeDataString(SharePointDiscoveryApi.WebSearchUri(Root, $"{Root}/sites/My%20Team").ToString());
        Assert.Contains($"Path:\"{Root}/sites/My Team/*\"", uri);
        Assert.Contains("contentclass:STS_List_DocumentLibrary", uri);
        Assert.StartsWith($"{Root}/_api/search/query?", uri);
    }

    [Fact]
    public void Volume_paths_follow_the_drives_names()
    {
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x"), [Documents, Archive], (_, _) => Task.FromResult(""), DriveScope.All);
        Assert.Equal(@"\Sites\Finance\Documents", ns.VolumePathOfLibrary(Documents.Key));
        Assert.Null(ns.VolumePathOfLibrary(Projects.Key));
    }
}

/// <summary>The discovery report behind `vfs365 why` and support bundles.</summary>
public sealed class DiscoveryReportTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 25, 9, 0, 0, TimeSpan.Zero);
    const string Root = "https://contoso.sharepoint.com";

    static LibraryEntry Library(string key, string site) => new(key, "s", "w", "l", $"{Root}/sites/{site}", site, "Documents", false, 1);

    static DiscoveryResult Result(IReadOnlyList<LibraryEntry> shown, IReadOnlyList<SkippedLibrary>? skipped = null, string? searchError = null) =>
        new(new MyDrive("b!me", "https://contoso-my.sharepoint.com/personal/a"), Root, shown, skipped ?? [], [], 4, searchError, null,
            new DiscoveryState(T0, Root, shown, new Dictionary<string, string>()));

    static DiscoveryState Before(params LibraryEntry[] libraries) =>
        new(T0.AddDays(-1), Root, libraries, new Dictionary<string, string>(), Misses: new Dictionary<string, int> { ["gone"] = 1 });

    [Fact]
    public void Libraries_a_run_stopped_showing_are_listed_with_the_runs_that_missed_them()
    {
        var report = DiscoveryReport.From(Result([Library("kept", "A")], [new SkippedLibrary("excluded", $"{Root}/sites/C", "Documents", "Site excluded by pattern")]),
            Before(Library("kept", "A"), Library("gone", "B"), Library("excluded", "C")), null, T0);
        var hidden = Assert.Single(report.Hidden);
        Assert.Equal(("gone", $"{Root}/sites/B", T0), (hidden.Key, hidden.WebUrl, hidden.Since));
        Assert.Contains("in 2 discovery runs in a row", hidden.Reason);
        Assert.Equal("Site excluded by pattern", Assert.Single(report.Skipped).Reason);
    }

    [Fact]
    public void A_failed_search_hides_nothing_and_another_tenant_isnt_compared()
    {
        Assert.Empty(DiscoveryReport.From(Result([], searchError: "Search failed after 0 rows: down"), Before(Library("gone", "B")), null, T0).Hidden);
        var other = Before(Library("gone", "B")) with { TenantRoot = "https://fabrikam.sharepoint.com" };
        Assert.Empty(DiscoveryReport.From(Result([]), other, null, T0).Hidden);
    }

    [Fact]
    public void Hidden_libraries_stay_listed_until_they_show_again_or_a_month_passed()
    {
        var first = DiscoveryReport.From(Result([]), Before(Library("gone", "B")), null, T0);
        var later = DiscoveryReport.From(Result([]), Before(), first, T0.AddDays(10));
        Assert.Equal(T0, Assert.Single(later.Hidden).Since);
        Assert.Empty(DiscoveryReport.From(Result([Library("gone", "B")]), Before(), first, T0.AddDays(10)).Hidden);
        Assert.Empty(DiscoveryReport.From(Result([]), Before(), first, T0 + DiscoveryReport.KeepHidden).Hidden);
    }

    [Fact]
    public void The_report_is_saved_and_read_back()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vfs365-report-{Guid.NewGuid():N}", DiscoveryReport.FileName);
        try
        {
            var report = DiscoveryReport.From(Result([], [new SkippedLibrary("k", $"{Root}/sites/C", "Site Assets", "System or catalog library", "SiteAssets")]),
                Before(Library("gone", "B")), null, T0);
            DiscoveryReport.Save(report, path);
            var read = DiscoveryReport.Load(path)!;
            Assert.Equal(report.At, read.At);
            Assert.Equal("SiteAssets", Assert.Single(read.Skipped).InternalName);
            Assert.Equal("gone", Assert.Single(read.Hidden).Key);
            File.WriteAllText(path, "{ damaged");
            Assert.Null(DiscoveryReport.Load(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
