using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Explorer searches on the drive: reading the search a tab shows, the words, the Microsoft 365 search pages, when to act.</summary>
public sealed class ExplorerSearchTests
{
    const string Clsid = "{3CB016A6-FE2C-45AC-ADBD-05A92CDE26CC}";

    [Fact]
    public void Explorers_search_location_gives_words_and_folder()
    {
        var search = ExplorerSearch.Parse(
            "search-ms:displayname=Search%20Results%20in%20Documents&crumb=System.Generic.String%3Abudget%202025&crumb=location:M%3A%5CSites%5CSales%5CDocuments");

        Assert.NotNull(search);
        Assert.Equal("budget 2025", search.Terms);
        Assert.Equal([@"M:\Sites\Sales\Documents"], search.Locations);
        Assert.False(search.HasFilters);
    }

    [Fact]
    public void Query_parameter_and_unc_location()
    {
        var search = ExplorerSearch.Parse(@"search-ms:query=offer&crumb=location:%5C%5CVFS365%5Cann%5COneDrive&");

        Assert.Equal("offer", search!.Terms);
        Assert.Equal([@"\\VFS365\ann\OneDrive"], search.Locations);
    }

    [Fact]
    public void Other_crumbs_are_filters()
    {
        var search = ExplorerSearch.Parse("search-ms:crumb=System.Generic.String%3Aplan&crumb=kind%3Adocument&crumb=location:M%3A%5C");

        Assert.Equal("plan", search!.Terms);
        Assert.True(search.HasFilters);
        Assert.Equal([@"M:\"], search.Locations);
    }

    [Fact]
    public void Several_locations_and_no_words()
    {
        var search = ExplorerSearch.Parse("search-ms:crumb=kind%3Apicture&crumb=location:M%3A%5COneDrive&crumb=location:C%3A%5CTemp");

        Assert.Equal("", search!.Terms);
        Assert.Equal([@"M:\OneDrive", @"C:\Temp"], search.Locations);
        Assert.True(search.HasFilters);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"M:\Sites\Sales")]
    [InlineData("file:///M:/Sites")]
    [InlineData("::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}")]
    public void Anything_else_is_no_search(string? location) => Assert.Null(ExplorerSearch.Parse(location));

    [Fact]
    public void Oversized_or_malformed_parts_do_no_harm()
    {
        Assert.Null(ExplorerSearch.Parse("search-ms:query=" + new string('a', 9000)));
        var search = ExplorerSearch.Parse("search-ms:=x&crumb&crumb=nocolon&query=100%&crumb=location:M%3A%5C");
        Assert.Equal("100%", search!.Terms);
        Assert.Equal([@"M:\"], search.Locations);
    }

    [Theory]
    [InlineData("::" + Clsid + @"\Sites\Sales", @"M:\", @"M:\Sites\Sales")]
    [InlineData("::" + Clsid, @"\\VFS365\ann", @"\\VFS365\ann\")]
    [InlineData("::" + Clsid + @"\OneDrive", @"\\VFS365\ann", @"\\VFS365\ann\OneDrive")]
    [InlineData("::" + Clsid + "X\\OneDrive", @"M:\", "::" + Clsid + "X\\OneDrive")]
    [InlineData(@"M:\OneDrive", @"M:\", @"M:\OneDrive")]
    [InlineData("::" + Clsid + @"\OneDrive", null, "::" + Clsid + @"\OneDrive")]
    public void Navigation_pane_locations_become_paths(string location, string? target, string expected) =>
        Assert.Equal(expected, ExplorerSearch.Unshell(location, Clsid, target));

    [Theory]
    [InlineData("  budget   2025 ", "budget 2025")]
    [InlineData("budget path:https://x.sharepoint.com/sites/y", "budget")]
    [InlineData("budget PATH:\"https://x.sharepoint.com/sites/a b\" plan", "budget plan")]
    [InlineData("\"q4 plan\" filetype:docx", "\"q4 plan\" filetype:docx")]
    [InlineData("\"unpaired quote", "unpaired quote")]
    [InlineData("budget OR plan", "budget OR plan")]
    [InlineData("budget kind:document", "budget")]
    [InlineData("kind:document", "")]
    [InlineData("offer ext:.docx", "offer filetype:docx")]
    [InlineData("offer type:pdf -fileext:\".xlsx\"", "offer filetype:pdf -filetype:xlsx")]
    [InlineData("plan datemodified:this week", "plan")]
    [InlineData("plan datemodified:2026-10-01 week", "plan week")]
    [InlineData("author:ann budget", "author:ann budget")]
    [InlineData(null, "")]
    public void Words_are_cleaned(string? text, string expected) => Assert.Equal(expected, SearchTerms.Clean(text));

    // Windows 11 25H2 search views as Shell Windows showed them in spike 15 (2026-10-26)
    static readonly string[] Roots = [@"M:\", @"Z:\", @"\\VFS365\JosLieben\"];

    [Theory]
    [InlineData(@"Search Results in M:\Sites\groep30&gre", "gre", @"M:\Sites\groep30", "gre", false)]
    [InlineData(@"Search Results in M:\Sites\groep30&budget kind:document", "budget kind:document", @"M:\Sites\groep30", "budget kind:document", true)]
    [InlineData(@"Search Results in \\VFS365\JosLieben\OneDrive&tessa", "tessa", @"\\VFS365\JosLieben\OneDrive", "tessa", false)]
    [InlineData(@"Zoekresultaten in M:\Sites\R&D&plan", "plan", @"M:\Sites\R&D", "plan", false)]
    [InlineData(@"Search Results in Z:\&q4 & more", "q4 & more", @"Z:\", "q4 & more", false)]
    [InlineData(@"Search Results in M:\Sites\BusinessProcesses", @"Search Results in M:\Sites\BusinessProcesses", @"M:\Sites\BusinessProcesses", "", false)]
    public void Windows_11_search_views_give_words_and_folder(string parsing, string name, string location, string terms, bool filters)
    {
        var search = ExplorerSearch.FromView(parsing, "", name, Roots, @"C:\elsewhere");

        Assert.NotNull(search);
        Assert.Equal(terms, search.Terms);
        Assert.Equal([location], search.Locations);
        Assert.Equal(filters, search.HasFilters);
    }

    [Fact]
    public void A_search_without_a_root_in_its_name_uses_the_folder_the_tab_showed_when_the_name_is_that_folder()
    {
        // A drive's top: LocationName says "VFS365" (spike 15), the drive's display name "VFS365 (M:)"
        Assert.Equal([@"M:\"], ExplorerSearch.FromView("Search Results in VFS365 (M:)&plan", null, "plan", Roots, @"M:\", "VFS365")!.Locations);
        Assert.Equal([@"M:\"], ExplorerSearch.FromView("Search Results in VFS365&plan", null, "plan", Roots, @"M:\", "VFS365")!.Locations);
        Assert.Equal([@"M:\"], ExplorerSearch.FromView("Zoekresultaten in VFS365 (M:)&plan", null, "plan", Roots, @"M:\", "vfs365")!.Locations);
        // Without a display name the folder's last path component stands in for it
        Assert.Equal([@"M:\Sites\X"], ExplorerSearch.FromView("Search Results in X&plan", null, "plan", Roots, @"M:\Sites\X")!.Locations);
        Assert.Equal([@"M:\Sites\X"], ExplorerSearch.FromView("Search Results in Team X&plan", null, "plan", Roots, @"M:\Sites\X", "Team X")!.Locations);
        Assert.Equal([@"M:\Sites\X"], ExplorerSearch.FromView("Search Results in X", null, "Search Results in X", Roots, @"M:\Sites\X", "X")!.Locations);
        Assert.Equal([@"::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"],
            ExplorerSearch.FromView("Search Results in This PC&tessa", null, "tessa", Roots, "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "This PC")!.Locations);
        Assert.Empty(ExplorerSearch.FromView("Search Results in This PC&tessa", null, "tessa", Roots, null)!.Locations);
    }

    [Theory]
    [InlineData("Search Results in This PC&tessa")]
    [InlineData("Search Results in Home&tessa")]
    [InlineData("Search Results in Quick access&tessa")]
    [InlineData("Zoekresultaten in Documenten&tessa")]
    [InlineData("Search Results in Libraries&tessa")]
    [InlineData(@"Search Results in C:\Users\jos\X&tessa")]
    [InlineData(@"Search Results in \\other\share\X&tessa")]
    [InlineData("Search Results in MyX&tessa")]
    [InlineData("Search Results in X Y&tessa")]
    public void A_search_elsewhere_is_no_search_in_the_folder_the_tab_showed_before(string parsing)
    {
        var search = ExplorerSearch.FromView(parsing, null, "tessa", Roots, @"M:\Sites\X", "X");

        Assert.NotNull(search);
        Assert.Equal("tessa", search.Terms);
        Assert.Empty(search.Locations);
        Assert.Empty(ExplorerSearch.FromView(parsing, null, "tessa", Roots, @"M:\Sites\X")!.Locations); // nor by its path alone
    }

    [Fact]
    public void A_search_in_a_place_with_a_path_names_that_path_whatever_the_tab_showed_before()
    {
        // The root in the name wins: the scope can be another folder of the drive than the one shown before
        Assert.Equal([@"M:\Sites\Y"], ExplorerSearch.FromView(@"Search Results in M:\Sites\Y&plan", null, "plan", Roots, @"M:\Sites\X", "X")!.Locations);
        Assert.Equal([@"M:\Sites\Y"], ExplorerSearch.FromView(@"Search Results in M:\Sites\Y&plan", null, "plan", Roots, "::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", "Home")!.Locations);
    }

    [Fact]
    public void Shell_names_are_no_search_views()
    {
        Assert.Null(ExplorerSearch.FromView("shell:::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", "", "Home", Roots, @"M:\Sites\X", "X"));
        Assert.Null(ExplorerSearch.FromView("SHELL:Libraries", "", "Libraries", Roots, @"M:\Sites\X", "X"));
    }

    [Theory]
    [InlineData(@"M:\Sites\groep30", "file:///M:/Sites/groep30", "groep30")]
    [InlineData(@"\\VFS365\JosLieben\OneDrive", "file://VFS365/JosLieben/OneDrive", "OneDrive")]
    [InlineData("::{F874310E-B6B7-47DC-BC84-B9E6B38F5903}", "", "Home")]
    [InlineData("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", "", "This PC")]
    [InlineData(null, null, null)]
    public void Folders_and_special_places_are_no_search(string? parsing, string? url, string? name) =>
        Assert.Null(ExplorerSearch.FromView(parsing, url, name, Roots, null));

    [Fact]
    public void A_search_ms_location_still_works() =>
        Assert.Equal("offer", ExplorerSearch.FromView(null, "search-ms:query=offer&crumb=location:M%3A%5C", "offer", Roots, null)!.Terms);

    [Fact]
    public void The_page_for_each_scope()
    {
        const string root = "https://contoso.sharepoint.com", site = "https://contoso.sharepoint.com/sites/x";
        // The tenant's search page for every scope: a personal site's own page found nothing for a path: query (0.7.2 test)
        Assert.Equal($"{root}/_layouts/15/search.aspx?q=offer", SearchUrls.For(new SearchScope(SearchArea.Everything, root), "offer"));
        Assert.Equal($"{root}/_layouts/15/search.aspx?q=" + Uri.EscapeDataString($"(offer) path:\"{site}\""),
            SearchUrls.For(new SearchScope(SearchArea.Site, root, site), "offer"));
        Assert.Equal($"{root}/_layouts/15/search.aspx?q=" + Uri.EscapeDataString($"(offer) path:\"{site}/Shared Documents/Q4\""),
            SearchUrls.For(new SearchScope(SearchArea.Folder, root, site, $"{site}/Shared%20Documents/Q4", $"{site}/Shared%20Documents"), "offer"));
        const string personal = "https://contoso-my.sharepoint.com/personal/ann_contoso_com";
        Assert.Equal($"{root}/_layouts/15/search.aspx?q=" + Uri.EscapeDataString($"(AGENTS) path:\"{personal}/Documents/BV\""),
            SearchUrls.For(new SearchScope(SearchArea.Folder, root, personal, $"{personal}/Documents/BV", $"{personal}/Documents"), "AGENTS"));
        Assert.Equal($"{root}/_layouts/15/search.aspx?q=offer", SearchUrls.For(new SearchScope(SearchArea.Site, root), "offer")); // no site known
        Assert.Null(SearchUrls.For(new SearchScope(SearchArea.Everything, "http://contoso.sharepoint.com"), "offer"));
    }

    [Fact]
    public void A_search_acts_once_its_words_settle()
    {
        var settle = new SearchSettle();
        long now = 20_000;
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, @"\Sites\X|bud", now));
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, @"\Sites\X|budget", now += 700));
        Assert.True(settle.Pending);
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, @"\Sites\X|budget", now += 1000));
        Assert.Equal(SearchSettle.Verdict.Act, settle.See(1, @"\Sites\X|budget", now += 600));
        Assert.False(settle.Pending);
        Assert.Equal(SearchSettle.Verdict.None, settle.See(1, @"\Sites\X|budget", now += 500)); // still on its way back
        Assert.Equal(SearchSettle.Verdict.StopAgain, settle.See(1, @"\Sites\X|budget", now += 2000)); // sending it back didn't work
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, @"\Sites\X|budget", now += SearchSettle.Repeat + 1)); // a new search later
    }

    [Fact]
    public void Typing_on_and_on_acts_after_the_longest_wait()
    {
        var settle = new SearchSettle();
        long now = 20_000;
        var verdict = SearchSettle.Verdict.Wait;
        for (var i = 1; i <= 20 && verdict == SearchSettle.Verdict.Wait; i++)
        {
            verdict = settle.See(7, new string('a', i), now += 500);
        }
        Assert.Equal(SearchSettle.Verdict.Act, verdict);
        Assert.True(now - 20_000 >= SearchSettle.MaxWait && now - 20_000 < SearchSettle.MaxWait + 1000);
    }

    [Fact]
    public void The_word_window_has_its_controls() =>
        Assert.Equal(["Static", "Edit", "Button", "Button"], AskWindow.HiddenControls(), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Leaving_a_search_forgets_it_and_tabs_are_separate()
    {
        var settle = new SearchSettle();
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, "a", 20_000));
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(2, "b", 20_000));
        Assert.Equal(SearchSettle.Verdict.None, settle.See(1, null, 20_500));
        Assert.Equal(SearchSettle.Verdict.Act, settle.See(2, "b", 21_600));
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, "a", 21_600)); // a new start for tab 1
    }

    [Fact]
    public void The_same_words_again_after_the_tab_went_back_are_a_new_search()
    {
        // Live test 2: AGENTS typed again 12 s after the hand-off counted as "still showing" and only went back
        var settle = new SearchSettle();
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, @"\OneDrive\BV|AGENTS", 0));
        Assert.Equal(SearchSettle.Verdict.Act, settle.See(1, @"\OneDrive\BV|AGENTS", 1600));
        Assert.Equal(SearchSettle.Verdict.None, settle.See(1, null, 2000)); // back on the folder
        Assert.Equal(SearchSettle.Verdict.Wait, settle.See(1, @"\OneDrive\BV|AGENTS", 12_000));
        Assert.Equal(SearchSettle.Verdict.Act, settle.See(1, @"\OneDrive\BV|AGENTS", 13_600));
    }

    [Fact]
    public void Words_are_capped() => Assert.Equal(SearchTerms.MaxLength, SearchTerms.Clean(new string('x', 400)).Length);

    [Fact]
    public void Scope_is_a_quoted_path_restriction()
    {
        Assert.Equal("(budget) path:\"https://contoso.sharepoint.com/sites/x/Shared Documents/Q4\"",
            SearchUrls.Kql("budget", "https://contoso.sharepoint.com/sites/x/Shared%20Documents/Q4/"));
        Assert.Equal("path:\"https://contoso.sharepoint.com/sites/x\"", SearchUrls.Kql("", "https://contoso.sharepoint.com/sites/x"));
        Assert.Null(SearchUrls.Kql("budget", "https://contoso.sharepoint.com/sites/x/a%22b"));
    }

    [Fact]
    public void Candidates_for_a_folder_are_encoded_and_on_the_tenant()
    {
        var scope = new SearchScope(SearchArea.Folder, "https://contoso.sharepoint.com", "https://contoso.sharepoint.com/sites/x",
            "https://contoso.sharepoint.com/sites/x/Shared%20Documents/Q4", "https://contoso.sharepoint.com/sites/x/Shared%20Documents");

        var candidates = SearchUrls.Candidates(scope, "budget & plan #1 ü");

        Assert.Contains(candidates, c => c.Id.StartsWith("P2") && c.Url ==
            "https://contoso.sharepoint.com/sites/x/_layouts/15/search.aspx/siteall?q=" +
            Uri.EscapeDataString("(budget & plan #1 ü) path:\"https://contoso.sharepoint.com/sites/x/Shared Documents/Q4\""));
        Assert.Contains(candidates, c => c.Id.StartsWith("L1"));
        Assert.All(candidates, c => Assert.StartsWith("https://contoso.sharepoint.com/", c.Url));
        Assert.All(candidates, c => Assert.DoesNotContain(" ", c.Url));
        Assert.DoesNotContain(candidates, c => c.Id.StartsWith("O1"));
    }

    [Fact]
    public void OneDrive_gets_its_own_page()
    {
        var scope = new SearchScope(SearchArea.OneDrive, "https://contoso.sharepoint.com", "https://contoso-my.sharepoint.com/personal/ann_contoso_com",
            "https://contoso-my.sharepoint.com/personal/ann_contoso_com/Documents", "https://contoso-my.sharepoint.com/personal/ann_contoso_com/Documents");

        Assert.Contains(SearchUrls.Candidates(scope, "offer"), c => c.Id.StartsWith("O1")
            && c.Url == "https://contoso-my.sharepoint.com/personal/ann_contoso_com/_layouts/15/onedrive.aspx?view=7&q=offer");
    }

    [Fact]
    public void Everything_has_only_tenant_pages()
    {
        var candidates = SearchUrls.Candidates(new SearchScope(SearchArea.Everything, "https://contoso.sharepoint.com/"), "offer");

        Assert.Equal(["E1 tenant search", "E2 tenant files"], candidates.Select(c => c.Id));
        Assert.Equal("https://contoso.sharepoint.com/_layouts/15/search.aspx?q=offer", candidates[0].Url);
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/_layouts/15/search.aspx?q=a", true)]
    [InlineData("https://contoso-my.sharepoint.com/personal/ann/_layouts/15/search.aspx?q=a", true)]
    [InlineData("http://contoso.sharepoint.com/_layouts/15/search.aspx?q=a", false)]
    [InlineData("https://evil.sharepoint.com/_layouts/15/search.aspx?q=a", false)]
    [InlineData("https://graph.microsoft.com/v1.0/search/query", false)]
    [InlineData("https://www.microsoft365.com/search?q=a", false)]
    [InlineData("ms-word:ofe|u|https://contoso.sharepoint.com/a.docx", false)]
    public void Only_tenant_https_pages_open(string url, bool safe) =>
        Assert.Equal(safe, SearchUrls.Safe(url, "https://contoso.sharepoint.com") is not null);

    [Fact]
    public void Overlong_urls_dont_open() =>
        Assert.Null(SearchUrls.Safe("https://contoso.sharepoint.com/_layouts/15/search.aspx?q=" + new string('a', 2100), "https://contoso.sharepoint.com"));
}
