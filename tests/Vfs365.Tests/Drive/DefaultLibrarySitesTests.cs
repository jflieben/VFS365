using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Policy DefaultLibrarySites: a site's folder is its default library, whatever other libraries the user can reach.</summary>
public sealed class DefaultLibrarySitesTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    static readonly MyDrive Me = new("b!me", "https://x-my.sharepoint.com/personal/u");

    // Internal names are localized ("Gedeelde documenten"): discovery's mark tells the default library
    static LibraryEntry Library(string site, string title, string internalName, bool? isDefault, string? template = "GROUP") =>
        new($"k-{site}-{internalName}", "s", "w", "l", $"https://x.sharepoint.com/sites/{site}", site, title, false, 1,
            DriveId: $"b!{site}-{internalName}".Replace(" ", ""), InternalName: internalName, SiteTemplate: template, DefaultLibrary: isDefault);

    static readonly LibraryEntry FinanceDocuments = Library("Finance", "Documenten", "Gedeelde documenten", true);
    static readonly LibraryEntry FinanceArchive = Library("Finance", "Archive", "Archive", false);
    static readonly LibraryEntry IntranetDocuments = Library("Intranet", "Documents", "Shared Documents", null, "SITEPAGEPUBLISHING");
    static readonly LibraryEntry IntranetPolicies = Library("Intranet", "Policies", "Policies", null, "SITEPAGEPUBLISHING");

    static DriveNamespace Build(IReadOnlyList<LibraryEntry> libraries, params string[] policy) =>
        DriveNamespace.Build(Me, libraries, (library, _) => Task.FromResult(library.DriveId!), defaultLibrarySites: new DefaultLibrarySites(policy));

    DriveEngine Engine(DriveNamespace ns, FakeDriveApi api) =>
        new(ns, api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging") });

    [Fact]
    public async Task A_matching_site_folder_is_its_default_library()
    {
        var api = new FakeDriveApi();
        api.Add("General", folder: true);
        api.Add("General/Budget.xlsx", content: "x");
        var engine = Engine(Build([FinanceDocuments, FinanceArchive, IntranetDocuments, IntranetPolicies], "GROUP"), api);

        var finance = await engine.ListAsync((await engine.GetEntryAsync(@"\Sites\Finance", default))!, default);
        var intranet = await engine.ListAsync((await engine.GetEntryAsync(@"\Sites\Intranet", default))!, default);
        var budget = await engine.GetEntryAsync(@"\Sites\Finance\General\Budget.xlsx", default);

        Assert.Equal(["General"], finance.Select(e => e.Name));
        Assert.Equal("b!Finance-Gedeeldedocumenten", await engine.DriveIdOfAsync(budget!, default));
        Assert.Null(await engine.GetEntryAsync(@"\Sites\Finance\Archive", default)); // the other library doesn't show
        Assert.Equal(["Documents", "Policies"], intranet.Select(e => e.Name).Order()); // another template: as before
    }

    [Fact]
    public void Paths_depend_on_the_policy_not_on_how_many_libraries_the_user_reaches()
    {
        var ns = Build([FinanceDocuments], "GROUP");
        Assert.Equal(@"\Sites\Finance", ns.VolumePathOf("b!Finance-Gedeeldedocumenten"));

        // A second library appears, or the user gets access to one: the default library's paths stay
        var changed = ns.SetLibraries([FinanceArchive, FinanceDocuments, Library("Finance", "New", "New", null)]);

        Assert.Equal(@"\Sites\Finance", ns.VolumePathOf("b!Finance-Gedeeldedocumenten"));
        Assert.Null(ns.VolumePathOf("b!Finance-Archive"));
        Assert.Null(ns.VolumePathOfLibrary(FinanceArchive.Key));
        Assert.Empty(changed);
    }

    [Fact]
    public async Task A_matching_site_without_its_default_library_has_no_folder()
    {
        // Archive: checked, not the default. Unchecked: discovery didn't look it up yet (the policy is new)
        var engine = Engine(Build([FinanceArchive, IntranetDocuments, Library("Unchecked", "Documents", "Shared Documents", null)], "GROUP"), new FakeDriveApi());

        var sites = await engine.ListAsync((await engine.GetEntryAsync(@"\Sites", default))!, default);

        Assert.Equal(["Intranet"], sites.Select(e => e.Name));
        Assert.Null(await engine.GetEntryAsync(@"\Sites\Finance", default));
    }

    [Fact]
    public void A_site_address_pattern_names_sites_of_any_template()
    {
        var ns = Build([IntranetDocuments with { DefaultLibrary = true }, IntranetPolicies with { DefaultLibrary = false }, FinanceDocuments, FinanceArchive],
            "https://x.sharepoint.com/sites/Intra*");

        Assert.Equal(@"\Sites\Intranet", ns.VolumePathOf("b!Intranet-SharedDocuments"));
        Assert.Equal(@"\Sites\Finance\Documenten", ns.VolumePathOf("b!Finance-Gedeeldedocumenten"));
        Assert.Equal(@"\Sites\Finance\Archive", ns.VolumePathOf("b!Finance-Archive"));
    }

    [Fact]
    public void Without_the_policy_nothing_changes()
    {
        var ns = Build([FinanceDocuments, FinanceArchive]);

        Assert.Equal(@"\Sites\Finance\Documenten", ns.VolumePathOf("b!Finance-Gedeeldedocumenten"));
        Assert.Equal(@"\Sites\Finance\Archive", ns.VolumePathOf("b!Finance-Archive"));
    }

    [Fact]
    public void With_scope_SharePoint_the_site_folder_at_the_root_is_the_library()
    {
        var ns = DriveNamespace.Build(Me, [FinanceDocuments, FinanceArchive], (library, _) => Task.FromResult(library.DriveId!), DriveScope.SharePoint,
            defaultLibrarySites: new DefaultLibrarySites(["GROUP"]));

        Assert.Equal(@"\Finance", ns.VolumePathOf("b!Finance-Gedeeldedocumenten"));
    }

    [Theory]
    [InlineData("GROUP", "https://x.sharepoint.com/sites/a", "GROUP", true)]
    [InlineData("group", "https://x.sharepoint.com/sites/a", "GROUP#0", true)] // a pinned site's template has a configuration number
    [InlineData("GROUP", "https://x.sharepoint.com/sites/a", "STS", false)]
    [InlineData("GROUP", "https://x.sharepoint.com/sites/a", null, false)] // a template search didn't report never matches
    [InlineData("SITEPAGEPUBLISHING#0", "https://x.sharepoint.com/sites/a", "SITEPAGEPUBLISHING", true)]
    [InlineData("*/sites/proj-*", "https://x.sharepoint.com/sites/Proj-42/", null, true)]
    [InlineData("*/sites/proj-*", "https://x.sharepoint.com/teams/proj-42", "GROUP", false)]
    public void Entries_are_templates_or_address_patterns(string entry, string webUrl, string? template, bool matches) =>
        Assert.Equal(matches, new DefaultLibrarySites([entry]).Matches(webUrl, template));

    [Fact]
    public void Only_matching_sites_without_a_marked_library_need_a_lookup()
    {
        var policy = new DefaultLibrarySites(["GROUP"]);
        var notLookedUp = Library("Sales", "Documents", "Shared Documents", null);

        Assert.Equal(["https://x.sharepoint.com/sites/Sales"], policy.Unchecked([FinanceDocuments, FinanceArchive, notLookedUp, IntranetDocuments]).Select(w => w.Key));
        Assert.Empty(policy.Unchecked([FinanceArchive])); // checked: the default library isn't among the user's
        Assert.Empty(DefaultLibrarySites.None.Unchecked([notLookedUp]));
        Assert.True(new DefaultLibrarySites([" ", ""]).IsEmpty);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
