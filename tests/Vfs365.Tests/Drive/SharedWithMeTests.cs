using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>
/// The Shared with me folder: shared folders are mounts rooted at their path in the owner's drive, shared files open read-only, nothing
/// is created, renamed or deleted at the folder's own level, and a drive mounted in several places maps back to all of them. The fake
/// drive serves every drive ID, so the owner's drive is the fake's tree.
/// </summary>
public sealed class SharedWithMeTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly List<EngineChange> changes = [];
    readonly List<string> log = [];
    DateTimeOffset now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);
    int listed;

    static readonly MyDrive Me = new("b!me", "https://x-my.sharepoint.com/personal/u");

    const string Shared = "\\Shared with me";

    static SharedItem Folder(string path, string owner = "Alice", string drive = "b!alice", bool readOnly = false, string? id = null) =>
        new(drive, id ?? $"id-{path}", path[(path.LastIndexOf('/') + 1)..], true, path, owner, $"{owner.ToLowerInvariant()}@x.com", ReadOnly: readOnly,
            Modified: new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero));

    static SharedItem File(string path, string owner = "Alice", string drive = "b!alice", string? id = null) =>
        new(drive, id ?? $"id-{path}", path[(path.LastIndexOf('/') + 1)..], false, path, owner, $"{owner.ToLowerInvariant()}@x.com", Size: 3);

    static LibraryEntry Library(string title, string driveId) =>
        new($"s|w|{title}", "s", "w", title, "https://x.sharepoint.com/sites/Team", "Team", title, false, 1, driveId);

    DriveEngine Create(IReadOnlyList<SharedItem>? shared, DriveScope scope = DriveScope.All, IReadOnlyList<LibraryEntry>? libraries = null) =>
        new(DriveNamespace.Build(Me, libraries ?? [], (_, _) => Task.FromResult("unused"), scope, shared: shared),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                Random = () => 0.5,
                Log = line => { lock (log) { log.Add(line); } },
                Changed = change => { lock (changes) { changes.Add(change); } },
                SharedListed = () => listed++,
            });

    static async Task<List<string>> NamesAsync(DriveEngine engine, string path) =>
        [.. (await engine.ListAsync((await engine.GetEntryAsync(path, default))!, default)).Select(e => e.Name)];

    [Fact]
    public async Task The_folder_shows_next_to_OneDrive_and_Sites_only_with_Scope_All_and_the_list_on()
    {
        Assert.Equal(["OneDrive", "Shared with me", "Sites"], await NamesAsync(Create([]), "\\"));
        Assert.Equal(["OneDrive", "Sites"], await NamesAsync(Create(null), "\\"));
        Assert.Empty(await NamesAsync(Create([Folder("Docs/Project")], DriveScope.SharePoint), "\\"));
        Assert.DoesNotContain("Shared with me", await NamesAsync(Create([Folder("Docs/Project")], DriveScope.OneDrive), "\\"));
    }

    [Fact]
    public async Task Shared_folders_and_files_are_listed_by_name_with_the_owner_when_names_clash()
    {
        var engine = Create(
        [
            Folder("Docs/Project"),
            Folder("Work/Project", owner: "Bob", drive: "b!bob"),
            File("Docs/Report.docx"),
            File("Notes/Report.docx", owner: "Bob", drive: "b!bob"),
            File("Docs/Budget.xlsx"),
            new SharedItem("b!carol", "id-x", "Nowhere", true), // its place in the owner's drive isn't known: not shown
        ]);

        var entries = await engine.ListAsync((await engine.GetEntryAsync(Shared, default))!, default);

        Assert.Equal(["Budget.xlsx", "Project (Alice)", "Project (Bob)", "Report (Alice).docx", "Report (Bob).docx"], entries.Select(e => e.Name));
        Assert.True(entries.Single(e => e.Name == "Project (Bob)").IsDirectory);
        Assert.All(entries.Where(e => !e.IsDirectory), e => Assert.True(e.ReadOnly));
        Assert.Equal(3, entries.Single(e => e.Name == "Budget.xlsx").Size);
        Assert.Equal(1, listed);
    }

    [Fact]
    public void Names_depend_on_the_set_not_on_the_order_search_returned_it()
    {
        SharedItem[] items =
        [
            File("A/Report.docx"), File("B/Report.docx", id: "id-other"), File("C/Report.docx", owner: "Bob", drive: "b!bob"),
            Folder("A/Plans"), Folder("B/Plans", owner: "Alice", drive: "b!alice2"),
        ];
        var forward = DriveNamespace.SharedNames(items);
        var backward = DriveNamespace.SharedNames([.. items.Reverse()]);

        Assert.Equal(forward.OrderBy(n => n.Key), backward.OrderBy(n => n.Key));
        // Two of Alice's with one name: numbered in key order
        Assert.Equal("Report (Alice).docx", forward["b!alice|id-A/Report.docx"]);
        Assert.Equal("Report (Alice) (2).docx", forward["b!alice|id-other"]);
        Assert.Equal("Report (Bob).docx", forward["b!bob|id-C/Report.docx"]);
        Assert.Equal("Plans (Alice)", forward["b!alice2|id-B/Plans"]); // ordinal key order: '2' before '|'
        Assert.Equal("Plans (Alice) (2)", forward["b!alice|id-A/Plans"]);
    }

    [Fact]
    public void Two_owners_with_the_same_name_are_told_apart_by_address()
    {
        var names = DriveNamespace.SharedNames(
        [
            File("A/Plan.docx", owner: "Jan") with { OwnerEmail = "jan.a@x.com" },
            File("B/Plan.docx", owner: "Jan", drive: "b!jan2") with { OwnerEmail = "jan.b@x.com" },
            File("C/Plan.docx", owner: "Bob", drive: "b!bob"),
        ]);

        Assert.Equal(["Plan (Bob).docx", "Plan (jan.a@x.com).docx", "Plan (jan.b@x.com).docx"], names.Values.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_remembered_name_never_opens_another_persons_item()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Report.docx", content: "alice");
        var engine = Create([File("Docs/Report.docx")]);
        Assert.NotNull(await engine.GetEntryAsync(Shared + "\\Report.docx", default));

        // Bob shares a file with the same name: neither keeps the bare name
        engine.SetShared([File("Docs/Report.docx"), File("Other/Report.docx", owner: "Bob", drive: "b!bob")]);

        Assert.Null(await engine.GetEntryAsync(Shared + "\\Report.docx", default));
        Assert.Contains(changes, c => c.Path == Shared);
    }

    [Fact]
    public async Task A_shared_folder_browses_and_saves_inside_the_owners_drive_at_its_path()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Project", folder: true);
        api.Add("Docs/Project/plan.txt", content: "v1");
        api.Add("Docs/Project/sub", folder: true);
        api.Add("Docs/secret.txt", content: "not shared");
        var engine = Create([Folder("Docs/Project")]);
        var top = (await engine.GetEntryAsync(Shared + "\\Project", default))!;

        Assert.True(top.IsDirectory);
        Assert.False(top.ReadOnly);
        Assert.Equal(["plan.txt", "sub"], await NamesAsync(engine, Shared + "\\Project"));

        // Create, write, rename and delete inside it: explicit user actions, in the owner's drive below the shared folder
        var created = await engine.CreateAsync(Shared + "\\Project\\new.txt", false, default);
        await System.IO.File.WriteAllTextAsync(created.StagingPath!, "new");
        engine.MarkDirty(created);
        await engine.CommitAsync(Shared + "\\Project\\new.txt", default);
        await engine.RenameAsync(Shared + "\\Project\\new.txt", Shared + "\\Project\\sub\\moved.txt", false, default);
        await engine.CreateAsync(Shared + "\\Project\\Folder", true, default);
        await engine.DeleteAsync(Shared + "\\Project\\plan.txt", default);
        now += TimeSpan.FromMinutes(1);
        await engine.ProcessDueAsync(true, default);
        await engine.WaitForUploadsAsync();

        Assert.Equal(["Docs", "Docs/Project", "Docs/Project/Folder", "Docs/Project/sub", "Docs/Project/sub/moved.txt", "Docs/secret.txt"], api.Paths);
        Assert.Equal("new", api.ContentOf("Docs/Project/sub/moved.txt"));
    }

    [Fact]
    public async Task A_Word_save_in_a_shared_folder_goes_into_the_same_item()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Project", folder: true);
        var original = api.Add("Docs/Project/report.docx", content: "v1");
        var engine = Create([Folder("Docs/Project")]);
        const string Folder_ = Shared + "\\Project\\";

        var temp = await engine.CreateAsync(Folder_ + "~WRD0000.tmp", false, default);
        await System.IO.File.WriteAllTextAsync(temp.StagingPath!, "v2");
        engine.MarkDirty(temp);
        await engine.CommitAsync(Folder_ + "~WRD0000.tmp", default);
        await engine.RenameAsync(Folder_ + "report.docx", Folder_ + "~WRL0001.tmp", false, default);
        await engine.RenameAsync(Folder_ + "~WRD0000.tmp", Folder_ + "report.docx", false, default);
        await engine.DeleteAsync(Folder_ + "~WRL0001.tmp", default);
        now += TimeSpan.FromMinutes(10);
        await engine.ProcessDueAsync(true, default);
        await engine.WaitForUploadsAsync();

        var upload = Assert.Single(api.Uploads);
        Assert.Equal(original.Id, upload.ItemId);
        Assert.Equal("v2", api.ContentOf("Docs/Project/report.docx"));
        Assert.Equal(["Docs", "Docs/Project", "Docs/Project/report.docx"], api.Paths);
        Assert.Equal(0, api.Deletes);
    }

    [Fact]
    public async Task A_view_only_shared_folder_is_read_only()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Project", folder: true);
        api.Add("Docs/Project/plan.txt", content: "v1");
        var engine = Create([Folder("Docs/Project", readOnly: true)]);

        Assert.True((await engine.GetEntryAsync(Shared + "\\Project", default))!.ReadOnly);
        Assert.True((await engine.GetEntryAsync(Shared + "\\Project\\plan.txt", default))!.ReadOnly);
        var refused = await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync(Shared + "\\Project\\new.txt", false, default));
        Assert.Equal(FsError.ReadOnly, refused.Error);
        Assert.Empty(api.Uploads);
    }

    [Fact]
    public async Task A_shared_file_reads_under_its_shown_name_and_refuses_every_change()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Report.docx", content: "alice");
        api.Add("Docs/Other.docx", content: "not shared");
        var engine = Create([File("Docs/Report.docx"), File("Notes/Report.docx", owner: "Bob", drive: "b!bob")]);
        const string Path_ = Shared + "\\Report (Alice).docx";

        var entry = (await engine.GetEntryAsync(Path_, default))!;
        Assert.Equal("Report (Alice).docx", entry.Name);
        Assert.False(entry.IsDirectory);
        Assert.True(entry.ReadOnly);
        using (var content = await engine.OpenContentAsync(entry, default))
        {
            var buffer = new byte[5];
            Assert.Equal(5, await content.ReadAsync(0, buffer, default));
            Assert.Equal("alice", System.Text.Encoding.UTF8.GetString(buffer));
        }

        // Nothing below a file, and nothing changes at the folder's own level
        Assert.Null(await engine.GetEntryAsync(Path_ + "\\x", default));
        Assert.Equal(FsError.ReadOnly, (await Assert.ThrowsAsync<FsException>(() => engine.StageAsync(entry, false, default))).Error);
        Assert.Equal(FsError.ReadOnly, (await Assert.ThrowsAsync<FsException>(() => engine.DeleteAsync(Path_, default))).Error);
        Assert.Equal(FsError.ReadOnly, (await Assert.ThrowsAsync<FsException>(() => engine.CanDeleteAsync(entry, default))).Error);
        Assert.Equal(FsError.ReadOnly, (await Assert.ThrowsAsync<FsException>(() => engine.RenameAsync(Path_, Shared + "\\Renamed.docx", false, default))).Error);
        Assert.Equal(FsError.AccessDenied, (await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync(Shared + "\\~WRD0000.tmp", false, default))).Error);
        Assert.Equal(FsError.AccessDenied, (await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync(Shared + "\\New folder", true, default))).Error);
        await engine.ProcessDueAsync(true, default);

        Assert.Equal(["Docs", "Docs/Other.docx", "Docs/Report.docx"], api.Paths);
        Assert.Equal("alice", api.ContentOf("Docs/Report.docx"));
        Assert.Empty(api.Uploads);
        Assert.Equal(0, api.Deletes + api.Moves);
    }

    [Fact]
    public async Task A_shared_folder_itself_is_never_deleted_renamed_or_replaced_from_the_drive()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Project", folder: true);
        api.Add("Docs/Project/plan.txt", content: "v1");
        var engine = Create([Folder("Docs/Project")]);
        var top = (await engine.GetEntryAsync(Shared + "\\Project", default))!;

        Assert.Equal(FsError.AccessDenied, (await Assert.ThrowsAsync<FsException>(() => engine.CanDeleteAsync(top, default))).Error);
        Assert.Equal(FsError.AccessDenied, (await Assert.ThrowsAsync<FsException>(() => engine.DeleteAsync(Shared + "\\Project", default))).Error);
        Assert.Equal(FsError.AccessDenied, (await Assert.ThrowsAsync<FsException>(() => engine.RenameAsync(Shared + "\\Project", Shared + "\\Other", false, default))).Error);
        // A file moved out of the shared folder to the Shared with me level is refused too
        Assert.Equal(FsError.AccessDenied, (await Assert.ThrowsAsync<FsException>(() => engine.RenameAsync(Shared + "\\Project\\plan.txt", Shared + "\\plan.txt", false, default))).Error);
        await engine.ProcessDueAsync(true, default);

        Assert.Equal(["Docs", "Docs/Project", "Docs/Project/plan.txt"], api.Paths);
        Assert.Equal(0, api.Deletes + api.Moves);
    }

    [Fact]
    public async Task A_refused_delete_of_a_shared_folder_keeps_the_files_deleted_just_before_it()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Project", folder: true);
        api.Add("Docs/Project/a.txt", content: "a");
        api.Add("Docs/Project/b.txt", content: "b");
        api.Add("Docs/Other", folder: true);
        api.Add("Docs/Other/c.txt", content: "c");
        var engine = Create([Folder("Docs/Project"), Folder("Docs/Other")]);
        await engine.DeleteAsync(Shared + "\\Other\\c.txt", default); // in another shared folder: deleted as asked

        // As Windows deletes a folder: its files first, then the folder itself
        await engine.DeleteAsync(Shared + "\\Project\\a.txt", default);
        await engine.DeleteAsync(Shared + "\\Project\\b.txt", default);
        var top = (await engine.GetEntryAsync(Shared + "\\Project", default))!;
        await Assert.ThrowsAsync<FsException>(() => engine.CanDeleteAsync(top, default));
        now += TimeSpan.FromMinutes(1);
        await engine.ProcessDueAsync(true, default);

        Assert.Equal(["Docs", "Docs/Other", "Docs/Project", "Docs/Project/a.txt", "Docs/Project/b.txt"], api.Paths);
        Assert.Equal(["a.txt", "b.txt"], await NamesAsync(engine, Shared + "\\Project"));
        Assert.Contains(changes, c => c.Path == Shared + "\\Project\\a.txt" && c.Kind == ChangeKind.Added);
    }

    [Fact]
    public async Task A_drive_shown_only_through_shared_items_has_no_change_feed_and_lists_again_after_the_ttl()
    {
        api.Add("Docs", folder: true);
        api.Add("Docs/Project", folder: true);
        api.Add("Docs/Project/a", folder: true);
        api.Add("Docs/Project/a/plan.txt", content: "v1");
        var engine = Create([Folder("Docs/Project")]);

        await NamesAsync(engine, Shared + "\\Project");
        await NamesAsync(engine, Shared + "\\Project\\a");
        await engine.GetEntryAsync(Shared + "\\Project\\a\\plan.txt", default);
        for (var i = 0; i < 5; i++)
        {
            now += TimeSpan.FromSeconds(30);
            await engine.PollActiveAsync();
            await engine.PollAsync("b!alice");
        }
        Assert.Equal(0, api.DeltaCalls);
        Assert.Empty(engine.PushDrives());

        // Folders above the shared one can't be opened by the user: never looked up or listed
        await Task.Delay(100);
        lock (api.LookedUp)
        {
            Assert.DoesNotContain("Docs", api.LookedUp);
            Assert.DoesNotContain("", api.LookedUp);
        }
        var lists = api.ListCalls;
        api.RemoteEdit("Docs/Project/a/plan.txt", "v2");
        now += TimeSpan.FromMinutes(2);
        await NamesAsync(engine, Shared + "\\Project\\a");
        await Task.Delay(100);
        Assert.True(api.ListCalls > lists);
        Assert.Contains(changes, c => c.Path == Shared + "\\Project\\a\\plan.txt");
    }

    [Fact]
    public async Task Lookups_in_a_shared_folders_hidden_parent_never_list_it()
    {
        api.Add("Docs", folder: true);
        for (var i = 0; i < 8; i++)
        {
            api.Add($"Docs/f{i}.txt", content: "x");
        }
        var engine = Create([.. Enumerable.Range(0, 8).Select(i => File($"Docs/f{i}.txt"))]);

        for (var i = 0; i < 8; i++)
        {
            Assert.NotNull(await engine.GetEntryAsync($"{Shared}\\f{i}.txt", default));
        }
        await Task.Delay(100);

        Assert.Equal(0, api.ListCalls);
    }

    [Fact]
    public async Task A_change_in_a_drive_mounted_twice_reaches_both_places()
    {
        api.Add("Project", folder: true);
        api.Add("Project/plan.txt", content: "v1");
        var engine = Create([Folder("Project", drive: "b!team")], libraries: [Library("Documents", "b!team")]);
        await engine.PollAsync("b!team");
        await NamesAsync(engine, "\\Sites\\Team\\Documents\\Project");
        await NamesAsync(engine, Shared + "\\Project");

        api.RemoteEdit("Project/plan.txt", "v2");
        now += TimeSpan.FromSeconds(30);
        await engine.PollAsync("b!team");

        Assert.Contains(changes, c => c.Path == "\\Sites\\Team\\Documents\\Project\\plan.txt");
        Assert.Contains(changes, c => c.Path == Shared + "\\Project\\plan.txt");
        Assert.Equal(["\\Sites\\Team\\Documents\\Project\\plan.txt", Shared + "\\Project\\plan.txt"], engine.VolumePathsOf("b!team", "Project/plan.txt"));
    }

    [Fact]
    public void Volume_paths_map_a_drive_path_to_every_place_that_shows_it()
    {
        var ns = DriveNamespace.Build(Me, [Library("Documents", "b!team")], (_, _) => Task.FromResult("unused"), shared:
        [
            Folder("Project", drive: "b!team"),
            Folder("Docs/A"),
            Folder("Docs/B"),
            File("Docs/A/plan.docx"),
        ]);

        Assert.Equal(["\\Sites\\Team\\Documents"], ns.VolumePathsOf("b!team", ""));
        Assert.Equal(["\\Sites\\Team\\Documents\\Project\\x\\y.txt", Shared + "\\Project\\x\\y.txt"], ns.VolumePathsOf("b!team", "Project/x/y.txt"));
        Assert.Equal(["\\Sites\\Team\\Documents\\Projects"], ns.VolumePathsOf("b!team", "Projects"));
        Assert.Equal([Shared + "\\A\\plan.docx", Shared + "\\plan.docx"], ns.VolumePathsOf("b!alice", "Docs/A/plan.docx"));
        Assert.Equal([Shared + "\\B"], ns.VolumePathsOf("b!alice", "Docs/B"));
        Assert.Empty(ns.VolumePathsOf("b!alice", "Docs"));
        Assert.Empty(ns.VolumePathsOf("b!alice", "Docs/C/x.txt"));
        Assert.Empty(ns.VolumePathsOf("b!nobody", ""));
        Assert.Equal("\\OneDrive\\a.txt", ns.VolumePathsOf("b!me", "a.txt").Single());
        Assert.Equal(Shared + "\\A", ns.VolumePathOfShared("b!alice|id-Docs/A"));
    }

    [Fact]
    public async Task A_library_mount_resolved_later_is_found_by_its_drive_ID()
    {
        var library = Library("Documents", "b!team") with { DriveId = null };
        var ns = DriveNamespace.Build(Me, [library], (_, _) => Task.FromResult("b!team"), shared: [Folder("Project", drive: "b!team")]);
        Assert.Equal([Shared + "\\Project\\a.txt"], ns.VolumePathsOf("b!team", "Project/a.txt"));

        var engine = new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions { StagingDirectory = directory });
        await engine.DriveIdOfAsync((await engine.GetEntryAsync("\\Sites\\Team\\Documents", default))!, default);

        Assert.Equal(["\\Sites\\Team\\Documents\\Project\\a.txt", Shared + "\\Project\\a.txt"], ns.VolumePathsOf("b!team", "Project/a.txt"));
    }

    [Fact]
    public void Replacing_the_list_reports_the_folder_and_keeps_unchanged_mounts()
    {
        var ns = DriveNamespace.Build(Me, [], (_, _) => Task.FromResult("unused"), shared: [Folder("Docs/A")]);

        Assert.Empty(ns.SetShared([Folder("Docs/A")]));
        Assert.Equal([Shared], ns.SetShared([Folder("Docs/A"), File("Docs/b.txt")]));
        // Moved in the owner's drive: the same name shows another place
        Assert.Contains(Shared + "\\A", ns.SetShared([Folder("Other/A", id: "id-Docs/A"), File("Docs/b.txt")]));
        Assert.Contains("\\", ns.SetShared(null));
        Assert.False(ns.ShowsShared);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
        }
    }
}
