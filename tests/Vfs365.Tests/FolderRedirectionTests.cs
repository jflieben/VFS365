using Vfs365.Agent;

namespace Vfs365.Tests;

/// <summary>Known folders moved into the volume and back, durably, without taking folders that something else moved; drive letter rules.</summary>
public sealed class FolderRedirectionTests : IDisposable
{
    const string Unc = @"\\VFS365\jan";
    static readonly Guid DocumentsId = new("fdd39ad0-238f-46af-adb4-6c85480369c7"), ThisPcDocumentsId = new("f42ee2d3-909f-4907-8871-4c22fc0bf756");
    static readonly Guid DesktopId = new("b4bfcc3a-db2c-424c-b029-7fe99a87c641");

    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeKnownFolders folders = new();

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    FolderRedirection Create() => new(folders, Path.Combine(directory, "redirection.json"), path => VolumePaths.OnVolume(path, Unc, "M:"));

    static IReadOnlyList<RedirectRule> Rules(params string[] entries)
    {
        var problems = new List<string>();
        var rules = RedirectRule.Parse(entries, problems);
        Assert.Empty(problems);
        return rules;
    }

    [Fact]
    public void Entries_name_a_known_folder_a_target_and_options()
    {
        var problems = new List<string>();
        var rules = RedirectRule.Parse([@"Documents=OneDrive\Documents|move", @"My Pictures = OneDrive\Pictures", "{B4BFCC3A-DB2C-424C-B029-7FE99A87C641}=Desk",
            "Nothing=x", "Desktop", @"Music=OneDrive\Music|copy", @"documents=Elsewhere"], problems);

        Assert.Equal(["Documents", "Pictures", "Desktop"], rules.Select(r => r.Folder));
        Assert.Equal([DocumentsId, ThisPcDocumentsId], rules[0].Ids);
        Assert.True(rules[0].Move);
        Assert.False(rules[1].Move);
        Assert.Equal(4, problems.Count); // unknown folder, no target, unknown option, Documents twice
    }

    [Fact]
    public void A_folder_goes_into_the_volume_and_back_at_unmount()
    {
        var redirection = Create();
        var rules = Rules(@"Documents=OneDrive\Documents");

        var (done, left, _) = redirection.Apply([(rules[0], @"M:\OneDrive\Documents")], rules);
        Assert.Single(done);
        Assert.Empty(left);
        Assert.Equal(@"M:\OneDrive\Documents", folders.Get(DocumentsId));
        Assert.Equal(@"M:\OneDrive\Documents", folders.Get(ThisPcDocumentsId));

        Assert.Empty(Create().Apply([(rules[0], @"M:\OneDrive\Documents")], rules).Done); // again: nothing to do

        Assert.Single(Create().RestoreAll()); // another instance, as after a restart of the agent: the state file remembers
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(DocumentsId));
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(ThisPcDocumentsId));
        Assert.False(File.Exists(Path.Combine(directory, "redirection.json")));
    }

    [Fact]
    public void A_folder_moved_by_something_else_is_left_alone()
    {
        folders.Set(DocumentsId, @"C:\Users\jan\OneDrive - Contoso\Documents"); // OneDrive's folder backup
        var rules = Rules(@"Documents=OneDrive\Documents");

        var (done, left, _) = Create().Apply([(rules[0], @"M:\OneDrive\Documents")], rules);

        Assert.Empty(done);
        Assert.Contains("something else", Assert.Single(left));
        Assert.Equal(@"C:\Users\jan\OneDrive - Contoso\Documents", folders.Get(DocumentsId));
    }

    [Fact]
    public void After_a_crash_the_folder_is_taken_over_and_a_new_target_replaces_the_old_one()
    {
        var rules = Rules(@"Desktop=OneDrive\Desktop");
        Create().Apply([(rules[0], @"\\VFS365\jan\OneDrive\Desktop")], rules);
        // No unmount (a crash), then a policy with another target
        var moved = Rules(@"Desktop=Sites\Home\Documents\jan\Desktop");

        Assert.Single(Create().Apply([(moved[0], @"\\VFS365\jan\Sites\Home\Documents\jan\Desktop")], moved).Done);
        Assert.Equal(@"\\VFS365\jan\Sites\Home\Documents\jan\Desktop", folders.Get(DesktopId));

        Create().RestoreAll();
        Assert.Equal(FakeKnownFolders.Profile + "Desktop", folders.Get(DesktopId)); // the original from before the first move
    }

    [Fact]
    public void A_rule_taken_out_of_policy_puts_its_folder_back_and_a_target_not_known_now_keeps_it()
    {
        var rules = Rules(@"Documents=OneDrive\Documents", @"Desktop=OneDrive\Desktop");
        var redirection = Create();
        redirection.Apply([(rules[0], @"M:\OneDrive\Documents"), (rules[1], @"M:\OneDrive\Desktop")], rules);

        // Desktop's target isn't known now (offline, library not discovered yet): it stays; Documents left the policy: it goes back
        var desktopOnly = Rules(@"Desktop=OneDrive\Desktop");
        var (done, _, _) = redirection.Apply([], desktopOnly);

        Assert.Contains("no longer in policy", Assert.Single(done));
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(DocumentsId));
        Assert.Equal(@"M:\OneDrive\Desktop", folders.Get(DesktopId));
    }

    [Fact]
    public void A_folder_someone_moved_after_VFS365_is_not_put_back()
    {
        var rules = Rules(@"Documents=OneDrive\Documents");
        var redirection = Create();
        redirection.Apply([(rules[0], @"M:\OneDrive\Documents")], rules);
        folders.Set(DocumentsId, @"D:\Documents");

        Assert.Empty(redirection.RestoreAll());
        Assert.Equal(@"D:\Documents", folders.Get(DocumentsId));
    }

    [Fact]
    public void Move_asks_for_the_local_content_and_existing_names_stay()
    {
        var rules = Rules(@"Desktop=OneDrive\Desktop|move");
        var (_, _, moves) = Create().Apply([(rules[0], @"M:\OneDrive\Desktop")], rules);
        Assert.Equal((FakeKnownFolders.Profile + "Desktop", @"M:\OneDrive\Desktop"), Assert.Single(moves));

        var from = Path.Combine(directory, "local");
        var to = Path.Combine(directory, "volume");
        Directory.CreateDirectory(Path.Combine(from, "Projects", "2026"));
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "a.txt"), "a");
        File.WriteAllText(Path.Combine(from, "Projects", "2026", "plan.txt"), "plan");
        File.WriteAllText(Path.Combine(from, "desktop.ini"), "[.ShellClassInfo]");
        File.WriteAllText(Path.Combine(from, "both.txt"), "local");
        File.WriteAllText(Path.Combine(to, "both.txt"), "volume");

        var stayed = FolderRedirection.MoveContents(from, to);

        Assert.Contains("both.txt", Assert.Single(stayed));
        Assert.Equal("plan", File.ReadAllText(Path.Combine(to, "Projects", "2026", "plan.txt")));
        Assert.Equal("a", File.ReadAllText(Path.Combine(to, "a.txt")));
        Assert.Equal("volume", File.ReadAllText(Path.Combine(to, "both.txt")));
        Assert.False(Directory.Exists(Path.Combine(from, "Projects")));
        Assert.True(File.Exists(Path.Combine(from, "desktop.ini")));
        Assert.False(File.Exists(Path.Combine(to, "desktop.ini")));
    }

    [Fact]
    public void Policy_paths_are_taken_from_the_volume_root()
    {
        Assert.Equal(@"OneDrive\Desktop", VolumePaths.Relative(@"OneDrive\Desktop", Unc, "M:"));
        Assert.Equal(@"OneDrive\Desktop", VolumePaths.Relative(@"M:\OneDrive\Desktop", Unc, "M:"));
        Assert.Equal(@"OneDrive\Desktop", VolumePaths.Relative(@"\\vfs365\JAN\OneDrive\Desktop", Unc, null));
        Assert.Null(VolumePaths.Relative(@"D:\Desktop", Unc, "M:"));
        Assert.Null(VolumePaths.Relative(@"\\fileserver\home\jan", Unc, "M:"));
        Assert.Equal(@"M:\OneDrive", VolumePaths.Windows(@"\OneDrive", Unc, "M:"));
        Assert.Equal(@"\\VFS365\jan\OneDrive", VolumePaths.Windows(@"\OneDrive", Unc, null));
        Assert.Equal(@"M:\", VolumePaths.Windows(@"\", Unc, "M:"));
        Assert.True(VolumePaths.OnVolume(@"\\VFS365\jan\x", Unc, null));
        Assert.False(VolumePaths.OnVolume(@"OneDrive\x", Unc, "M:"));
    }

    [Fact]
    public void Drive_mappings_name_a_letter_a_folder_and_a_label()
    {
        var problems = new List<string>();
        var mappings = DriveMapping.Parse([@"z:=Sites\Finance\Documents|Finance", @"Y = OneDrive", @"M:=OneDrive", "Q:", @"*=OneDrive", @"Z:=Elsewhere"], "M:", problems);

        Assert.Equal([("Z:", @"Sites\Finance\Documents", "Finance"), ("Y:", "OneDrive", null)], mappings.Select(m => (m.Letter, m.Target, m.Label)));
        Assert.Equal(4, problems.Count); // the drive's own letter, no target, no letter, Z: twice
        Assert.True(SessionDriveLetters.IsSharePointWebDav(@"\\contoso.sharepoint.com@SSL\DavWWWRoot\sites\Finance\Shared Documents"));
        Assert.False(SessionDriveLetters.IsSharePointWebDav(@"\\fileserver\finance"));
    }

    sealed class FakeKnownFolders : IKnownFolderStore
    {
        public const string Profile = @"C:\Users\jan\";
        readonly Dictionary<Guid, string> paths = new()
        {
            [DocumentsId] = Profile + "Documents",
            [ThisPcDocumentsId] = Profile + "Documents",
            [DesktopId] = Profile + "Desktop",
        };

        public string? Get(Guid id) => paths.GetValueOrDefault(id);

        public string? Default(Guid id) => id == DesktopId ? Profile + "Desktop" : Profile + "Documents";

        public void Set(Guid id, string path) => paths[id] = path;

        public void Refresh()
        {
        }
    }
}
