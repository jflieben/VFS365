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

    string StatePath => Path.Combine(directory, "redirection.json");

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }
            Directory.Delete(directory, recursive: true);
        }
    }

    FolderRedirection Create() => new(folders, StatePath, path => VolumePaths.OnVolume(path, Unc, "M:"));

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

        Assert.Single(Create().RestoreAll().Done); // another instance, as after a restart of the agent: the state file remembers
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(DocumentsId));
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(ThisPcDocumentsId));
        Assert.False(File.Exists(StatePath));
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
    public void A_folder_found_where_it_belongs_after_a_crash_tells_Explorer_once()
    {
        var rules = Rules(@"Desktop=OneDrive\Desktop");
        Create().Apply([(rules[0], @"M:\OneDrive\Desktop")], rules);
        var refreshes = folders.Refreshes;

        var restarted = Create(); // the next start after a crash: the folder is in the volume already
        Assert.Empty(restarted.Apply([(rules[0], @"M:\OneDrive\Desktop")], rules).Done);
        Assert.Equal(refreshes + 1, folders.Refreshes);
        restarted.Apply([(rules[0], @"M:\OneDrive\Desktop")], rules); // hourly
        Assert.Equal(refreshes + 1, folders.Refreshes);
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
        folders.Set(ThisPcDocumentsId, @"D:\Documents");

        Assert.Empty(redirection.RestoreAll().Done);
        Assert.Equal(@"D:\Documents", folders.Get(DocumentsId));
    }

    [Fact]
    public void The_state_is_on_disk_before_a_folder_moves()
    {
        var rules = Rules(@"Documents=OneDrive\Documents");
        var recorded = new List<bool>();
        folders.BeforeSet = (_, path) => recorded.Add(path == @"M:\OneDrive\Documents" && File.ReadAllText(StatePath).Contains(@"M:\\OneDrive\\Documents", StringComparison.Ordinal));

        Create().Apply([(rules[0], @"M:\OneDrive\Documents")], rules);

        Assert.Equal([true, true], recorded); // a crash at any Set leaves a record to put it back from
        Assert.False(File.Exists(StatePath + ".tmp"));
    }

    [Fact]
    public void When_the_second_ID_fails_the_first_goes_back_and_nothing_is_recorded()
    {
        var rules = Rules(@"Documents=OneDrive\Documents");
        folders.FailSet = (id, path) => id == ThisPcDocumentsId && path.StartsWith("M:", StringComparison.Ordinal);

        var (done, left, _) = Create().Apply([(rules[0], @"M:\OneDrive\Documents")], rules);

        Assert.Empty(done);
        Assert.Contains("could not move it", Assert.Single(left));
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(DocumentsId));
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(ThisPcDocumentsId));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void A_state_file_that_is_not_valid_is_set_aside_not_overwritten()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(StatePath, "{ not json");
        var rules = Rules(@"Desktop=OneDrive\Desktop");

        Assert.Single(Create().Apply([(rules[0], @"M:\OneDrive\Desktop")], rules).Done);

        var aside = Assert.Single(Directory.GetFiles(directory, "redirection.json.unreadable-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
    }

    [Fact]
    public void A_state_file_that_can_not_be_opened_now_is_never_saved_over()
    {
        var rules = Rules(@"Desktop=OneDrive\Desktop");
        Create().Apply([(rules[0], @"M:\OneDrive\Desktop")], rules);
        var before = File.ReadAllText(StatePath);

        using (new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.None)) // antivirus, a backup
        {
            Assert.Throws<IOException>(() => Create().Apply([], Rules(@"Documents=OneDrive\Documents")));
            var (done, problems) = Create().RestoreAll();
            Assert.Empty(done);
            Assert.Contains("can't be read", Assert.Single(problems));
        }

        Assert.Equal(before, File.ReadAllText(StatePath));
        Assert.Equal(@"M:\OneDrive\Desktop", folders.Get(DesktopId));
        Assert.Single(Create().RestoreAll().Done); // once it opens, the folder goes back
    }

    [Fact]
    public void A_folder_that_fails_to_go_back_stays_recorded_and_the_others_go_back()
    {
        var rules = Rules(@"Documents=OneDrive\Documents", @"Desktop=OneDrive\Desktop");
        Create().Apply([(rules[0], @"M:\OneDrive\Documents"), (rules[1], @"M:\OneDrive\Desktop")], rules);
        folders.FailSet = (id, path) => id == DesktopId && !path.StartsWith("M:", StringComparison.Ordinal);

        var (done, problems) = Create().RestoreAll();

        Assert.Contains("Documents", Assert.Single(done));
        Assert.Contains("Desktop stays", Assert.Single(problems));
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(DocumentsId));
        folders.FailSet = null;
        Assert.Contains("Desktop", Assert.Single(Create().RestoreAll().Done)); // the next start or the uninstall
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void Nothing_moves_after_the_folders_went_back()
    {
        var redirection = Create();
        var rules = Rules(@"Documents=OneDrive\Documents");
        redirection.RestoreAll();

        Assert.Empty(redirection.Apply([(rules[0], @"M:\OneDrive\Documents")], rules).Done); // a layout run that was still going on
        Assert.Equal(FakeKnownFolders.Profile + "Documents", folders.Get(DocumentsId));
        Assert.False(File.Exists(StatePath));
    }

    [Fact]
    public void Move_asks_for_the_local_content_and_existing_names_stay()
    {
        var rules = Rules(@"Desktop=OneDrive\Desktop|move");
        var (_, _, moves) = Create().Apply([(rules[0], @"M:\OneDrive\Desktop")], rules);
        Assert.Equal(new PendingMove("Desktop", FakeKnownFolders.Profile + "Desktop", @"M:\OneDrive\Desktop"), Assert.Single(moves));

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
    public void A_move_left_halfway_goes_on_after_a_restart_until_it_finished()
    {
        var rules = Rules(@"Desktop=OneDrive\Desktop|move");
        Create().Apply([(rules[0], @"M:\OneDrive\Desktop")], rules);

        // A stop or crash before the move finished: the next start still has it to do, also with the folder in the volume already
        var restarted = Create();
        Assert.Single(restarted.Apply([(rules[0], @"M:\OneDrive\Desktop")], rules).Moves);
        var move = Assert.Single(restarted.PendingMoves());

        restarted.MoveFinished(move);
        Assert.Empty(Create().PendingMoves());
        Assert.Empty(Create().Apply([(rules[0], @"M:\OneDrive\Desktop")], Rules(@"Desktop=OneDrive\Desktop")).Moves);
    }

    [Fact]
    public void Moving_merges_into_folders_that_exist_and_leaves_what_SharePoint_or_Outlook_needs_here()
    {
        var from = Path.Combine(directory, "local");
        var to = Path.Combine(directory, "volume");
        Directory.CreateDirectory(Path.Combine(from, "Projects"));
        Directory.CreateDirectory(Path.Combine(to, "Projects"));
        File.WriteAllText(Path.Combine(from, "Projects", "new.txt"), "new");
        File.WriteAllText(Path.Combine(to, "Projects", "there.txt"), "there");
        File.WriteAllText(Path.Combine(from, "Outlook.pst"), "mail");
        File.WriteAllText(Path.Combine(from, "~$Report.docx"), "owner");
        File.WriteAllText(Path.Combine(from, "site_vti_cnf.txt"), "x");
        File.WriteAllText(Path.Combine(from, "Thumbs.db"), "thumbs");
        var deep = Path.Combine(from, new string('d', 200), new string('e', 200));
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "far.txt"), "far");

        var stayed = FolderRedirection.MoveContents(from, to);

        Assert.Equal(["new.txt", "there.txt"], Directory.GetFiles(Path.Combine(to, "Projects")).Select(Path.GetFileName).Order());
        Assert.False(Directory.Exists(Path.Combine(from, "Projects")));
        Assert.Contains(stayed, s => s.Contains("Outlook.pst", StringComparison.Ordinal) && s.Contains("Outlook data file", StringComparison.Ordinal));
        Assert.Contains(stayed, s => s.Contains("~$Report.docx", StringComparison.Ordinal));
        Assert.Contains(stayed, s => s.Contains("_vti_", StringComparison.Ordinal));
        Assert.Contains(stayed, s => s.Contains(new string('e', 200), StringComparison.Ordinal) && s.Contains("characters", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(from, "Outlook.pst")));
        Assert.True(File.Exists(Path.Combine(deep, "far.txt")));
        Assert.True(File.Exists(Path.Combine(from, "Thumbs.db")));
        Assert.DoesNotContain(stayed, s => s.Contains("Thumbs.db", StringComparison.Ordinal));
    }

    [Fact]
    public void A_file_in_use_stays_and_the_rest_moves()
    {
        var from = Path.Combine(directory, "local");
        var to = Path.Combine(directory, "volume");
        Directory.CreateDirectory(Path.Combine(from, "Work"));
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "Work", "open.xlsx"), "open");
        File.WriteAllText(Path.Combine(from, "Work", "closed.txt"), "closed");
        File.WriteAllText(Path.Combine(from, "later.txt"), "later");

        IReadOnlyList<string> stayed;
        using (new FileStream(Path.Combine(from, "Work", "open.xlsx"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            stayed = FolderRedirection.MoveContents(from, to);
        }

        Assert.Contains("open.xlsx", Assert.Single(stayed));
        Assert.True(File.Exists(Path.Combine(to, "Work", "closed.txt")));
        Assert.True(File.Exists(Path.Combine(to, "later.txt")));
        Assert.True(File.Exists(Path.Combine(from, "Work", "open.xlsx")));
        Assert.False(File.Exists(Path.Combine(to, "Work", "open.xlsx")));

        Assert.Empty(FolderRedirection.MoveContents(from, to)); // the next start's move takes it
        Assert.True(File.Exists(Path.Combine(to, "Work", "open.xlsx")));
        Assert.False(Directory.Exists(Path.Combine(from, "Work")));
    }

    [Fact]
    public void Moving_stops_when_the_drive_stops()
    {
        var from = Path.Combine(directory, "local");
        var to = Path.Combine(directory, "volume");
        Directory.CreateDirectory(from);
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(from, "a.txt"), "a");
        using var stopped = new CancellationTokenSource();
        stopped.Cancel();

        Assert.Throws<OperationCanceledException>(() => FolderRedirection.MoveContents(from, to, stopped.Token));
        Assert.True(File.Exists(Path.Combine(from, "a.txt")));
    }

    [Theory]
    [InlineData("Report.docx", 30, null)]
    [InlineData("~$Report.docx", 30, "~$")]
    [InlineData("a_vti_b.txt", 30, "_vti_")]
    [InlineData(".lock", 30, ".lock")]
    [InlineData("COM1.txt", 30, "COM1")]
    [InlineData("nul", 30, "nul")]
    [InlineData("Report.docx", 401, "401 characters")]
    [InlineData("Report.docx", 400, null)]
    public void Names_and_paths_SharePoint_refuses(string name, int pathLength, string? reason)
    {
        var refused = SharePointNames.Refused(name, pathLength);
        if (reason is null)
        {
            Assert.Null(refused);
        }
        else
        {
            Assert.Contains(reason, refused);
        }
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

    [Fact]
    public void A_free_letter_for_the_drive_is_none_a_mapping_wants()
    {
        var mappings = DriveMapping.Parse([@"Z:=Sites\Finance\Documents", @"X:=OneDrive"], null, new List<string>());

        Assert.Equal("W:", DriveMapping.FreeLetter(mappings, letter => letter == "Y:"));
        Assert.Equal("Z:", DriveMapping.FreeLetter([], _ => false));
        Assert.Null(DriveMapping.FreeLetter(mappings, _ => true));
    }

    [Theory]
    [InlineData(@"\??\UNC\VFS365\jan\Sites\Finance\Documents", true)]
    [InlineData(@"\??\UNC\vfs365\JAN\OneDrive", true)]
    [InlineData(@"\??\UNC\VFS365\jan", false)]               // the volume itself, never a folder letter
    [InlineData(@"\??\UNC\VFS365\janet\OneDrive", false)]    // another user's
    [InlineData(@"\??\UNC\fileserver\share", false)]
    [InlineData(@"\Device\Volume{1234}", false)]
    [InlineData(null, false)]
    public void Letters_a_crashed_agent_left_are_recognised_as_its_own(string? target, bool ours) =>
        Assert.Equal(ours, SessionDriveLetters.Ours(target, Unc));

    sealed class FakeKnownFolders : IKnownFolderStore
    {
        public const string Profile = @"C:\Users\jan\";
        readonly Dictionary<Guid, string> paths = new()
        {
            [DocumentsId] = Profile + "Documents",
            [ThisPcDocumentsId] = Profile + "Documents",
            [DesktopId] = Profile + "Desktop",
        };

        public Func<Guid, string, bool>? FailSet { get; set; }

        public Action<Guid, string>? BeforeSet { get; set; }

        public int Refreshes { get; private set; }

        public string? Get(Guid id) => paths.GetValueOrDefault(id);

        public string? Default(Guid id) => id == DesktopId ? Profile + "Desktop" : Profile + "Documents";

        public void Set(Guid id, string path)
        {
            BeforeSet?.Invoke(id, path);
            if (FailSet?.Invoke(id, path) == true)
            {
                throw new System.Runtime.InteropServices.COMException("Access is denied", unchecked((int)0x80070005));
            }
            paths[id] = path;
        }

        public void Refresh() => Refreshes++;
    }
}
