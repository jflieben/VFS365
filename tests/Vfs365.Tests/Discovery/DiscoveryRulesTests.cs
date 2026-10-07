using Vfs365.Core.Discovery;

namespace Vfs365.Tests.Discovery;

public class DiscoveryRulesTests
{
    [Theory]
    [InlineData("https://contoso-my.sharepoint.com/personal/jos_contoso_com/Documents", "https://contoso.sharepoint.com")]
    [InlineData("https://contosoeur-my.sharepoint.com/personal/x/Documents", "https://contosoeur.sharepoint.com")]
    [InlineData("https://agency-my.sharepoint.us/personal/x/Documents", "https://agency.sharepoint.us")]
    public void TenantRoot_drops_my_from_the_onedrive_host(string oneDrive, string expected)
    {
        Assert.Equal(expected, SharePointUrls.TenantRootFromOneDriveUrl(oneDrive));
    }

    [Theory]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared%20Documents/Forms/AllItems.aspx", "https://contoso.sharepoint.com/sites/Finance")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Sub/Docs/Forms/AllItems.aspx", "https://contoso.sharepoint.com/sites/Finance/Sub")]
    [InlineData("https://contoso.sharepoint.com/sites/Finance/Shared Documents", "https://contoso.sharepoint.com/sites/Finance")]
    [InlineData("https://contoso.sharepoint.com/Shared Documents/Forms/AllItems.aspx", "https://contoso.sharepoint.com")]
    public void WebUrl_is_the_library_parent(string path, string expected)
    {
        Assert.Equal(expected, SharePointUrls.WebUrlFromListPath(path));
    }

    [Theory]
    [InlineData("https://x.sharepoint.com/sites/HR-Team", "*/sites/HR*", true)]
    [InlineData("https://x.sharepoint.com/sites/HR-Team", "*/sites/HR", false)]
    [InlineData("https://x.sharepoint.com/sites/PWA", "*/sites/pwa", true)]
    [InlineData("https://x.sharepoint.com/sites/a.b", "*/sites/a?b", true)]
    [InlineData("https://x.sharepoint.com/sites/axxb", "*/sites/a.b", false)]
    public void Wildcard_behaves_like_powershell_like(string input, string pattern, bool expected)
    {
        Assert.Equal(expected, Wildcard.IsMatch(input, pattern));
    }

    static ListMetadata List(string title = "Documents", bool hidden = false, int? template = 101, string? feature = "00bfea71-e717-4e80-aa17-d0c71b360101",
        bool forceCheckout = false, bool offline = false, string? internalName = "Shared Documents") =>
        new(title, hidden, template, false, false, feature, forceCheckout, offline, 10, internalName);

    [Fact]
    public void Standard_library_is_shown_read_write()
    {
        Assert.Equal(new LibraryVerdict(true, LibraryAccess.ReadWrite, null, false), LibraryRules.Evaluate(List()));
    }

    [Fact]
    public void Checkout_library_is_shown_read_only()
    {
        var verdict = LibraryRules.Evaluate(List(forceCheckout: true));
        Assert.True(verdict.Include);
        Assert.Equal(LibraryAccess.ReadOnly, verdict.Access);
    }

    [Theory]
    [InlineData("Siteactiva", "SiteAssets")]
    [InlineData("Stijlbibliotheek", "Style Library")]
    [InlineData("Formuliersjablonen", "FormServerTemplates")]
    [InlineData("Preservation Hold Library", "PreservationHoldLibrary")]
    public void System_libraries_are_excluded_by_internal_name_in_any_language(string title, string internalName)
    {
        var verdict = LibraryRules.Evaluate(List(title, internalName: internalName));
        Assert.False(verdict.Include);
        Assert.True(verdict.Static);
    }

    [Fact]
    public void Titles_alone_exclude_nothing()
    {
        Assert.True(LibraryRules.Evaluate(List("Site Assets", internalName: "Gedeelde documenten")).Include);
    }

    [Fact]
    public void Hidden_wiki_and_other_templates_are_excluded_for_good()
    {
        Assert.True(LibraryRules.Evaluate(List(hidden: true)).Static);
        Assert.True(LibraryRules.Evaluate(List(template: 700)).Static);
        Assert.True(LibraryRules.Evaluate(List(feature: "{00BFEA71-C796-4402-9F2F-0EB9A6E71B18}")).Static);
    }

    [Fact]
    public void Offline_client_exclusion_is_rechecked_every_run()
    {
        var verdict = LibraryRules.Evaluate(List(offline: true));
        Assert.False(verdict.Include);
        Assert.False(verdict.Static);
    }
}
