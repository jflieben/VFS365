using System.Text;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Failures, handles and crashes on the write path: nothing a user saved is lost, spliced or deleted by accident.</summary>
public sealed class WriteIntegrityTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly List<EngineNotice> notices = [];
    DateTimeOffset now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";

    string Staging => Path.Combine(directory, "staging");

    DriveEngine Create(bool background = false, TimeSpan repeatWindow = default) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Staging,
                Clock = () => now,
                UploadNewFilesInBackground = background,
                RepeatSaveWindow = repeatWindow,
                Notice = notice => { lock (notices) { notices.Add(notice); } },
            });

    static async Task<FsEntry> WriteNewAsync(DriveEngine engine, string name, string content, bool close = true)
    {
        var entry = await engine.CreateAsync(Root + name, false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        if (close)
        {
            await engine.CommitAsync(Root + name, default);
        }
        return entry;
    }

    static async Task<FsEntry> SaveAsync(DriveEngine engine, string name, string content, bool close = true)
    {
        var entry = await engine.StageAsync((await engine.GetEntryAsync(Root + name, default))!, truncate: true, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        if (close)
        {
            await engine.CommitAsync(Root + name, default);
        }
        return entry;
    }

    async Task DueAsync(DriveEngine engine, TimeSpan later)
    {
        now += later;
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
    }

    static async Task<string> ReadAllAsync(DriveEngine engine, FsEntry file)
    {
        using var source = await engine.OpenContentAsync(file, default);
        var buffer = new byte[source.Length];
        var read = await source.ReadAsync(0, buffer, default);
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    [Fact]
    public async Task A_refused_delete_shows_the_file_again_and_other_work_goes_on()
    {
        var locked = api.Add("locked.txt", content: "keep");
        api.Add("data.bin", content: "v1");
        api.FailDelete = id => id == locked.Id ? new RemoteException(RemoteError.Locked, "423 locked") : null;
        var engine = Create();

        await engine.DeleteAsync(Root + "locked.txt", default);
        var mapped = (await engine.GetEntryAsync(Root + "data.bin", default))!;
        await File.WriteAllTextAsync(await engine.OpenForWriteAsync(mapped, truncate: true, default), "v2");
        engine.MarkDirty(mapped);
        engine.CommitLater(mapped);
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal("v2", api.ContentOf("data.bin"));
        Assert.NotNull(await engine.GetEntryAsync(Root + "locked.txt", default));
        Assert.Equal("keep", api.ContentOf("locked.txt"));
        var notice = Assert.Single(notices, n => n.Kind == NoticeKind.NotDeleted);
        Assert.Equal("locked.txt", notice.Name);
    }

    [Fact]
    public async Task Unmount_never_throws_on_a_refused_delete()
    {
        var protectedFile = api.Add("protected.txt");
        api.FailDelete = _ => new RemoteException(RemoteError.AccessDenied, "403 accessDenied");
        var engine = Create();
        await engine.DeleteAsync(Root + "protected.txt", default);
        await WriteNewAsync(engine, "new.txt", "x");

        await engine.ProcessDueAsync(all: true, default);

        Assert.NotNull(api.ItemAt("protected.txt"));
        Assert.Equal(protectedFile.Id, (await engine.GetEntryAsync(Root + "protected.txt", default))!.ItemId);
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task An_upload_that_fails_on_sign_in_is_retried()
    {
        api.Add("notes.txt", content: "v1");
        var engine = Create();
        api.FailUpload = _ => new InvalidOperationException("sign-in needed (the token source's own error)");

        await SaveAsync(engine, "notes.txt", "v2");
        Assert.Equal("v1", api.ContentOf("notes.txt"));
        Assert.Equal(1, engine.WaitingToUpload());
        Assert.Single(notices, n => n.Kind == NoticeKind.UploadWaiting);

        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Assert.Equal("v2", api.ContentOf("notes.txt"));
        Assert.Empty(engine.Unsaved());
        Assert.Single(notices, n => n.Kind == NoticeKind.UploadDone);
    }

    [Fact]
    public async Task An_upload_refused_with_403_is_retried_with_back_off()
    {
        api.Add("notes.txt", content: "v1");
        var engine = Create();
        api.FailUpload = _ => new RemoteException(RemoteError.AccessDenied, "403 accessDenied");

        await SaveAsync(engine, "notes.txt", "v2");
        await DueAsync(engine, TimeSpan.FromSeconds(31));
        await DueAsync(engine, TimeSpan.FromSeconds(31)); // second retry waits 60 s
        Assert.Equal(1, engine.WaitingToUpload());

        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromSeconds(31));
        Assert.Equal("v2", api.ContentOf("notes.txt"));
        Assert.Equal(0, engine.WaitingToUpload());
    }

    [Fact]
    public async Task A_file_deleted_on_the_server_while_edited_is_saved_as_a_new_file()
    {
        var original = api.Add("plan.txt", content: "v1");
        var engine = Create();
        var entry = await SaveAsync(engine, "plan.txt", "mine", close: false);
        api.RemoteDelete("plan.txt");

        await engine.CommitAsync(Root + "plan.txt", default);

        Assert.Equal("mine", api.ContentOf("plan.txt"));
        Assert.NotEqual(original.Id, api.ItemAt("plan.txt")!.Id);
        Assert.Empty(engine.Unsaved());
        Assert.Equal("mine", await ReadAllAsync(engine, entry));
    }

    [Fact]
    public async Task A_file_deleted_and_recreated_on_the_server_while_edited_becomes_a_conflict_copy()
    {
        api.Add("plan.txt", content: "v1");
        var engine = Create();
        await SaveAsync(engine, "plan.txt", "mine", close: false);
        api.RemoteDelete("plan.txt");
        api.Add("plan.txt", content: "theirs");

        await engine.CommitAsync(Root + "plan.txt", default);

        Assert.Equal("theirs", api.ContentOf("plan.txt"));
        var copy = Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal("mine", api.ContentOf(copy));
    }

    [Fact]
    public async Task Conflict_copies_in_the_same_second_get_their_own_names()
    {
        api.Add("notes.txt", content: "v1");
        api.FailUpload = target => target.ItemId is not null ? new RemoteException(RemoteError.Locked, "423") : null;
        var engine = Create();

        await SaveAsync(engine, "notes.txt", "first");
        await SaveAsync(engine, "notes.txt", "second");

        var copies = api.Paths.Where(p => p.Contains("(conflict", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, copies.Count);
        Assert.Equal(["first", "second"], copies.Select(c => api.ContentOf(c)!).Order());
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task Writes_through_a_handle_after_a_conflict_copy_go_into_the_copy()
    {
        api.Add("notes.txt", content: "v1");
        var engine = Create();
        var handle = await SaveAsync(engine, "notes.txt", "mine", close: false);
        api.RemoteEdit("notes.txt", "theirs");
        await engine.CommitAsync(Root + "notes.txt", default);
        var copy = Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));

        // The same handle writes again: it follows its content into the copy, never into the colleague's version
        Assert.Null(engine.BeginWrite(handle));
        var followed = engine.Follow(handle);
        Assert.Equal(copy, followed.Name);
        Assert.Equal("mine", await ReadAllAsync(engine, handle));
        var restaged = await engine.StageAsync(followed, truncate: false, default);
        await File.AppendAllTextAsync(restaged.StagingPath!, " more");
        engine.MarkDirty(restaged);
        await engine.CommitAsync(restaged.Path, default);

        Assert.Equal("theirs", api.ContentOf("notes.txt"));
        Assert.Equal("mine more", api.ContentOf(copy));
    }

    [Fact]
    public async Task Writes_through_a_handle_to_a_deleted_file_are_refused()
    {
        api.Add("notes.txt", content: "v1");
        var engine = Create();
        var handle = await SaveAsync(engine, "notes.txt", "mine", close: false);

        await engine.DeleteAsync(Root + "notes.txt", default);

        Assert.Null(engine.BeginWrite(handle));
        Assert.Equal(FsError.Invalid, Assert.Throws<FsException>(() => engine.Follow(handle)).Error);
        engine.MarkDirty(handle);
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task A_handle_on_a_new_file_reads_it_after_its_upload()
    {
        var engine = Create(background: true);
        var handle = await WriteNewAsync(engine, "new.txt", "hello");
        await engine.WaitForUploadsAsync();

        Assert.Equal("hello", await ReadAllAsync(engine, handle));
        Assert.Equal(api.ItemAt("new.txt")!.Id, engine.Follow(handle).ItemId);
    }

    [Fact]
    public async Task A_handle_opened_before_a_save_reads_the_saved_content()
    {
        api.Add("report.txt", content: "old");
        var engine = Create();
        var reader = (await engine.GetEntryAsync(Root + "report.txt", default))!;
        Assert.Equal("old", await ReadAllAsync(engine, reader));

        await SaveAsync(engine, "report.txt", "new content");

        Assert.Equal("new content", await ReadAllAsync(engine, reader));
    }

    [Fact]
    public async Task An_upload_ends_only_after_a_write_in_progress_and_that_write_uploads_next()
    {
        api.Add("data.txt", content: "v1");
        var engine = Create();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => { started.TrySetResult(); return hold.Task; };
        var handle = await SaveAsync(engine, "data.txt", "v2", close: false);
        var close = engine.CommitAsync(Root + "data.txt", default);
        await started.Task;

        // A write is in progress when the upload ends; the end waits for it (all on this thread: the lease is thread bound)
        WriteHeld(engine, handle, hold, close);
        await close;
        Assert.Equal(1, engine.WaitingToUpload());

        api.BeforeUpload = null;
        await DueAsync(engine, TimeSpan.FromSeconds(5));
        Assert.Equal("v3", api.ContentOf("data.txt"));
        Assert.Empty(engine.Unsaved());
    }

    static void WriteHeld(DriveEngine engine, FsEntry handle, TaskCompletionSource hold, Task close)
    {
        using var lease = engine.BeginWrite(handle) ?? throw new InvalidOperationException("no lease");
        File.WriteAllText(handle.StagingPath!, "v3");
        engine.MarkDirty(handle);
        hold.SetResult();
        Thread.Sleep(200);
        Assert.False(close.IsCompleted);
    }

    [Fact]
    public async Task Open_files_don_t_count_as_waiting_to_upload()
    {
        api.Add("notes.txt", content: "v1");
        var engine = Create();

        await SaveAsync(engine, "notes.txt", "editing", close: false);

        Assert.Single(engine.Unsaved());
        Assert.Equal(0, engine.WaitingToUpload());
    }

    [Fact]
    public async Task A_crash_after_the_upload_but_before_the_journal_went_sends_nothing_again()
    {
        var original = api.Add("notes.txt", content: "v1");
        var engine = Create();
        await SaveAsync(engine, "notes.txt", "v2 saved", close: false);
        await WriteNewAsync(engine, "new.txt", "new saved", close: false);
        // What the agent uploaded just before it died
        api.RemoteEdit("notes.txt", "v2 saved");
        api.Add("new.txt", content: "new saved");
        var uploads = api.Uploads.Count;

        var restarted = Create();
        Assert.Equal(2, restarted.Recover());
        await DueAsync(restarted, TimeSpan.Zero);

        Assert.Equal(uploads, api.Uploads.Count);
        Assert.DoesNotContain(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal(original.Id, api.ItemAt("notes.txt")!.Id);
        Assert.Empty(restarted.Unsaved());
        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task A_recovered_file_that_differs_on_the_server_still_ends_in_a_conflict_copy()
    {
        api.Add("notes.txt", content: "v1");
        var engine = Create();
        api.FailUpload = _ => new RemoteException(RemoteError.Unavailable, "503");
        await SaveAsync(engine, "notes.txt", "mine");
        api.FailUpload = null;
        api.RemoteEdit("notes.txt", "them");

        var restarted = Create();
        restarted.Recover();
        await DueAsync(restarted, TimeSpan.Zero);

        Assert.Equal("them", api.ContentOf("notes.txt"));
        Assert.Equal("mine", api.ContentOf(Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task A_recovered_file_that_was_never_flushed_goes_only_into_a_recovered_copy()
    {
        var original = api.Add("notes.txt", content: "v1");
        var engine = Create();
        await SaveAsync(engine, "notes.txt", "maybe zeros", close: false);
        await WriteNewAsync(engine, "new.txt", "new, maybe zeros", close: false);

        var restarted = Create();
        Assert.Equal(2, restarted.Recover());
        await DueAsync(restarted, TimeSpan.Zero);

        Assert.Equal("v1", api.ContentOf("notes.txt"));
        Assert.Equal(original.Id, api.ItemAt("notes.txt")!.Id);
        Assert.Equal("maybe zeros", api.ContentOf(Assert.Single(api.Paths, p => p.StartsWith("notes (recovered ", StringComparison.Ordinal))));
        Assert.Equal("new, maybe zeros", api.ContentOf(Assert.Single(api.Paths, p => p.StartsWith("new (recovered ", StringComparison.Ordinal))));
        Assert.DoesNotContain("new.txt", api.Paths);
        Assert.Empty(restarted.Unsaved());
        Assert.Equal(2, notices.Count(n => n.Kind == NoticeKind.ConflictCopy));
    }

    [Fact]
    public async Task A_journal_from_before_the_flushed_marker_recovers_into_its_item()
    {
        // 0.4.x flushed staging at every close and wrote no Flushed field: after the upgrade its saves go into their items
        var original = api.Add("notes.txt", content: "v1");
        var engine = Create();
        await SaveAsync(engine, "notes.txt", "saved offline", close: false);
        foreach (var journal in Directory.EnumerateFiles(Staging, "*.staging.json"))
        {
            var record = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(journal))!.AsObject();
            record.Remove("Flushed");
            File.WriteAllText(journal, record.ToJsonString());
        }

        var restarted = Create();
        Assert.Equal(1, restarted.Recover());
        await DueAsync(restarted, TimeSpan.Zero);

        Assert.Equal("saved offline", api.ContentOf("notes.txt"));
        Assert.Equal(original.Id, api.ItemAt("notes.txt")!.Id);
        Assert.DoesNotContain(api.Paths, p => p.Contains("(recovered ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Own_save_then_a_colleagues_save_shows_theirs_and_the_next_save_builds_on_it()
    {
        // P1: Alice saves, Bob saves later, Alice opens: she reads Bob's version, not hers, and her next save goes on top of it
        api.Add("plan.txt", content: "v1");
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        await SaveAsync(engine, "plan.txt", "alice");
        now += TimeSpan.FromMinutes(20);
        api.RemoteEdit("plan.txt", "bob");
        await engine.PollAsync("b!me");

        var opened = (await engine.GetEntryAsync(Root + "plan.txt", default))!;
        Assert.Equal("bob", await ReadAllAsync(engine, opened));

        var staged = await engine.StageAsync(opened, truncate: false, default);
        await File.AppendAllTextAsync(staged.StagingPath!, " + alice again");
        engine.MarkDirty(staged);
        await engine.CommitAsync(Root + "plan.txt", default);

        Assert.Equal("bob + alice again", api.ContentOf("plan.txt"));
        Assert.Equal(["plan.txt"], api.Paths);
    }

    [Fact]
    public async Task A_handle_on_an_own_save_that_writes_after_a_colleagues_save_ends_in_a_conflict_copy()
    {
        api.Add("plan.txt", content: "v1");
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        await SaveAsync(engine, "plan.txt", "alice");
        var handle = (await engine.GetEntryAsync(Root + "plan.txt", default))!;
        Assert.Equal("alice", await ReadAllAsync(engine, handle));
        api.RemoteEdit("plan.txt", "bob");
        await engine.PollAsync("b!me");

        // The handle read Alice's version: its in-place write never lands in Bob's
        var staged = await engine.StageAsync(engine.Follow(handle), truncate: false, default);
        Assert.Equal("alice", await File.ReadAllTextAsync(staged.StagingPath!));
        await File.AppendAllTextAsync(staged.StagingPath!, " edited");
        engine.MarkDirty(staged);
        await engine.CommitAsync(Root + "plan.txt", default);

        Assert.Equal("bob", api.ContentOf("plan.txt"));
        Assert.Equal("alice edited", api.ContentOf(Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task A_handle_on_a_version_before_a_colleagues_save_never_writes_into_theirs()
    {
        api.Add("plan.txt", content: "v1");
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        var handle = (await engine.GetEntryAsync(Root + "plan.txt", default))!;
        Assert.Equal("v1", await ReadAllAsync(engine, handle));
        api.RemoteEdit("plan.txt", "bob");
        await engine.PollAsync("b!me");

        var staged = await engine.StageAsync(handle, truncate: false, default);
        await File.AppendAllTextAsync(staged.StagingPath!, " edited");
        engine.MarkDirty(staged);
        await engine.CommitAsync(Root + "plan.txt", default);

        Assert.Equal("bob", api.ContentOf("plan.txt"));
        Assert.Equal("v1 edited", api.ContentOf(Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal))));
        Assert.DoesNotContain(api.Uploads, u => u.IfMatch is { } ifMatch && ifMatch.StartsWith("c", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_colleagues_save_read_by_the_feed_ends_the_follow_to_an_own_save()
    {
        api.Add("plan.txt", content: "v1");
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        var before = (await engine.GetEntryAsync(Root + "plan.txt", default))!;
        await SaveAsync(engine, "plan.txt", "alice");
        await engine.PollAsync("b!me"); // the echo of the own save
        Assert.NotSame(before, engine.Follow(before));
        api.RemoteEdit("plan.txt", "bob");
        await engine.PollAsync("b!me");

        Assert.Same(before, engine.Follow(before));
        Assert.Equal("bob", await ReadAllAsync(engine, (await engine.GetEntryAsync(Root + "plan.txt", default))!));
    }

    [Fact]
    public async Task A_metadata_change_on_the_server_does_not_end_a_save_in_a_conflict_copy()
    {
        var original = api.Add("log.txt", content: "v1");
        var engine = Create();
        await SaveAsync(engine, "log.txt", "v2", close: false);
        api.RemoteMetadataEdit("log.txt"); // a column or Document ID: new eTag, same content

        await engine.CommitAsync(Root + "log.txt", default);

        Assert.Equal("v2", api.ContentOf("log.txt"));
        Assert.Equal(original.Id, api.ItemAt("log.txt")!.Id);
        Assert.DoesNotContain(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_upload_Microsoft_365_refuses_is_not_retried_and_stays_unsaved()
    {
        var engine = Create(background: true);
        api.FailUpload = _ => new RemoteException(RemoteError.Rejected, "400 invalidRequest: the name isn't allowed");
        await WriteNewAsync(engine, "report.txt", "x");
        await engine.WaitForUploadsAsync();
        var attempts = api.Uploads.Count;

        await DueAsync(engine, TimeSpan.FromHours(1));
        await DueAsync(engine, TimeSpan.FromHours(1));

        Assert.Single(engine.Unsaved());
        Assert.Equal(0, engine.WaitingToUpload());
        Assert.Single(notices, n => n.Kind == NoticeKind.UploadWaiting);
        Assert.Empty(api.Paths);

        // Renamed, it is tried again
        api.FailUpload = null;
        await engine.RenameAsync(Root + "report.txt", Root + "report2.txt", false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal("x", api.ContentOf("report2.txt"));
        Assert.Equal(attempts, api.Uploads.Count - 1);
    }

    [Fact]
    public async Task A_rename_save_into_an_item_deleted_on_the_server_creates_it_anew()
    {
        api.Add("book.xlsx", content: "v1");
        var engine = Create();
        await WriteNewAsync(engine, "8A3F1C2D", "v2");
        await engine.DeleteAsync(Root + "book.xlsx", default);
        api.RemoteDelete("book.xlsx");

        await engine.RenameAsync(Root + "8A3F1C2D", Root + "book.xlsx", false, default);

        Assert.Equal("v2", api.ContentOf("book.xlsx"));
        Assert.Equal(["book.xlsx"], api.Paths);
    }

    [Fact]
    public async Task Changes_nobody_closed_upload_at_unmount()
    {
        api.Add("data.txt", content: "v1");
        var engine = Create();
        await SaveAsync(engine, "data.txt", "v2 open", close: false);

        await engine.ProcessDueAsync(all: true, default);

        Assert.Equal("v2 open", api.ContentOf("data.txt"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task Word_saving_a_file_with_changes_not_uploaded_yet_deletes_nothing()
    {
        // The last save waits for the repeat window, then Word saves again: the original with those changes steps aside and is deleted
        var original = api.Add("report.docx", content: "v1");
        var engine = Create(repeatWindow: TimeSpan.FromSeconds(30));
        await SaveAsync(engine, "report.docx", "v2");
        await SaveAsync(engine, "report.docx", "v3 waiting");
        Assert.Single(engine.Unsaved());

        await WriteNewAsync(engine, "~WRD0000.tmp", "v4");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default);
        Assert.Null(await engine.GetEntryAsync(Root + "report.docx", default));
        await engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default);
        await engine.DeleteAsync(Root + "~WRL0001.tmp", default);
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal(0, api.Deletes);
        Assert.Equal(original.Id, api.ItemAt("report.docx")!.Id);
        Assert.Equal("v4", api.ContentOf("report.docx"));
        Assert.Equal(["report.docx"], api.Paths);
    }

    [Fact]
    public async Task A_file_with_changes_renamed_to_a_temp_name_and_left_there_goes_back_and_uploads()
    {
        var original = api.Add("notes.txt", content: "v1");
        var engine = Create(repeatWindow: TimeSpan.FromSeconds(30));
        await SaveAsync(engine, "notes.txt", "v2");
        await SaveAsync(engine, "notes.txt", "v3 waiting");

        await engine.RenameAsync(Root + "notes.txt", Root + "notes.tmp", false, default);
        await DueAsync(engine, TimeSpan.FromSeconds(5));
        Assert.Equal("v2", api.ContentOf("notes.txt"));

        await DueAsync(engine, TimeSpan.FromMinutes(1));
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Assert.Equal("v3 waiting", api.ContentOf("notes.txt"));
        Assert.Equal(original.Id, api.ItemAt("notes.txt")!.Id);
        Assert.Equal(["notes.txt"], api.Paths);
        Assert.Null(await engine.GetEntryAsync(Root + "notes.tmp", default));
    }

    [Fact]
    public async Task A_file_with_changes_renamed_to_a_temp_name_goes_back_after_a_crash_and_its_temp_name_reaches_nothing()
    {
        var original = api.Add("notes.txt", content: "v1");
        var engine = Create(repeatWindow: TimeSpan.FromSeconds(30));
        await SaveAsync(engine, "notes.txt", "v2");
        await SaveAsync(engine, "notes.txt", "v3 waiting");
        await engine.RenameAsync(Root + "notes.txt", Root + "notes.tmp", false, default);

        var restarted = Create();
        Assert.Equal(1, restarted.Recover());
        await DueAsync(restarted, TimeSpan.FromMinutes(1));
        await DueAsync(restarted, TimeSpan.FromMinutes(1));

        Assert.Equal("v3 waiting", api.ContentOf("notes.txt"));
        Assert.Equal(original.Id, api.ItemAt("notes.txt")!.Id);
        Assert.Null(await restarted.GetEntryAsync(Root + "notes.tmp", default));
        await Assert.ThrowsAsync<FsException>(() => restarted.DeleteAsync(Root + "notes.tmp", default));
        await DueAsync(restarted, TimeSpan.FromMinutes(1));
        Assert.Equal(0, api.Deletes);
    }

    [Fact]
    public async Task A_claimed_original_renamed_to_a_real_name_never_takes_the_save_along()
    {
        // Word's backup copy: the original steps aside, the save goes into its item, then the original is renamed to the backup name
        var original = api.Add("report.docx", content: "v1");
        var engine = Create();
        await WriteNewAsync(engine, "~WRD0000.tmp", "v2");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default);
        await engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default);

        // v1 isn't cached here: no backup can be made, the rename is refused
        await Assert.ThrowsAsync<FsException>(() => engine.RenameAsync(Root + "~WRL0001.tmp", Root + "Backup of report.wbk", false, default));
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal(original.Id, api.ItemAt("report.docx")!.Id);
        Assert.Equal("v2", api.ContentOf("report.docx"));
        Assert.Equal(["report.docx"], api.Paths);
        Assert.Equal(0, api.Moves + api.Deletes);
    }

    [Fact]
    public async Task A_claimed_original_renamed_to_a_real_name_becomes_a_copy_of_the_version_before()
    {
        var original = api.Add("report.docx", content: "v1");
        api.GrowOnUpload = 10; // SharePoint writes properties into Office files: v1 stays cached next to the upload
        var engine = Create();
        Assert.Equal("v1", await ReadAllAsync(engine, (await engine.GetEntryAsync(Root + "report.docx", default))!));
        await WriteNewAsync(engine, "~WRD0000.tmp", "v2");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default);
        await engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default);

        await engine.RenameAsync(Root + "~WRL0001.tmp", Root + "Backup of report.wbk", false, default);
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal(original.Id, api.ItemAt("report.docx")!.Id);
        Assert.Equal("v2", api.ContentOf("report.docx"));
        Assert.Equal("v1", api.ContentOf("Backup of report.wbk"));
        Assert.Equal(0, api.Moves + api.Deletes);
    }

    [Fact]
    public async Task An_unclaimed_original_renamed_onto_another_file_replaces_its_content()
    {
        // A save backing out onto another name: its content goes into that item, which keeps its identity, and its own item goes
        api.Add("report.docx", content: "v1");
        var other = api.Add("other.docx", content: "o1");
        var engine = Create();
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default);
        await engine.RenameAsync(Root + "~WRL0001.tmp", Root + "other.docx", true, default);

        Assert.Null(await engine.GetEntryAsync(Root + "~WRL0001.tmp", default));
        Assert.Null(await engine.GetEntryAsync(Root + "report.docx", default));
        Assert.Equal("v1", await ReadAllAsync(engine, (await engine.GetEntryAsync(Root + "other.docx", default))!));
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal(other.Id, api.ItemAt("other.docx")!.Id);
        Assert.Equal("v1", api.ContentOf("other.docx"));
        Assert.Equal(["other.docx"], api.Paths);
        Assert.Equal(0, api.Moves);
    }

    [Fact]
    public async Task A_new_file_carries_its_creator_until_it_is_uploaded()
    {
        // The front end checks opens against the creating handle's sharing through it
        var creator = new object();
        var engine = Create();
        var created = await engine.CreateAsync(Root + "new.txt", false, default, creator);
        await File.WriteAllTextAsync(created.StagingPath!, "abc");
        engine.MarkDirty(created);

        Assert.Same(creator, created.Creator);
        Assert.Same(creator, (await engine.GetEntryAsync(Root + "new.txt", default))!.Creator);
        Assert.Same(creator, Assert.Single(await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default)).Creator);
        await engine.CommitAsync(created, default);
        Assert.Null((await engine.GetEntryAsync(Root + "new.txt", default))!.Creator);
    }

    [Fact]
    public async Task Lookups_and_listings_never_show_staged_content_deleted_meanwhile_as_empty()
    {
        // Staged content deleted between a lookup's look at local state and its size: the lookup shows what is there now
        const int files = 200;
        for (var i = 0; i < files; i++)
        {
            api.Add($"f{i}.txt", content: "abc");
        }
        var engine = Create();
        var root = (await engine.GetEntryAsync(Root.TrimEnd('\\'), default))!;
        var current = 0;
        var empty = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, 3).Select(r => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var name = $"f{Volatile.Read(ref current)}.txt";
                if (r == 0)
                {
                    foreach (var listed in (await engine.ListAsync(root, default)).Where(e => e.Size == 0))
                    {
                        empty.Enqueue($"listed {listed.Name}");
                    }
                }
                else if (await engine.GetEntryAsync(Root + name, default) is { Size: 0 })
                {
                    empty.Enqueue($"found {name}");
                }
            }
        })).ToList();
        for (var i = 0; i < files; i++)
        {
            Volatile.Write(ref current, i);
            await engine.StageAsync((await engine.GetEntryAsync($"{Root}f{i}.txt", default))!, truncate: false, default);
            await engine.DeleteAsync($"{Root}f{i}.txt", default);
        }
        stop.Cancel();
        await Task.WhenAll(readers);

        Assert.Empty(empty);
    }

    [Fact]
    public async Task Deleting_a_folder_after_its_files_sends_one_delete()
    {
        api.Add("old", folder: true);
        for (var i = 0; i < 30; i++)
        {
            api.Add($"old/f{i}.txt");
        }
        var engine = Create();
        foreach (var file in await engine.ListAsync((await engine.GetEntryAsync(Root + "old", default))!, default))
        {
            await engine.DeleteAsync(file.Path, default);
        }
        await engine.DeleteAsync(Root + "old", default);
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal(1, api.Deletes);
        Assert.Empty(api.Paths);
    }

    [Fact]
    public async Task A_bulk_delete_does_not_hold_up_saves()
    {
        for (var i = 0; i < 20; i++)
        {
            api.Add($"f{i}.txt");
        }
        api.Add("notes.txt", content: "v1");
        var engine = Create();
        for (var i = 0; i < 20; i++)
        {
            await engine.DeleteAsync(Root + $"f{i}.txt", default);
        }
        var network = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeDelete = () => network.Task;

        now += TimeSpan.FromMinutes(1);
        await engine.ProcessDueAsync(false, default);
        await SaveAsync(engine, "notes.txt", "v2"); // while every delete is still waiting for the network

        Assert.Equal("v2", api.ContentOf("notes.txt"));
        Assert.Equal(0, api.Deletes);
        Assert.Null(await engine.GetEntryAsync(Root + "f3.txt", default));
        network.SetResult();
        await engine.WaitForUploadsAsync();
        Assert.Equal(20, api.Deletes);
        Assert.Equal(["notes.txt"], api.Paths);
    }

    [Fact]
    public async Task A_rename_whose_move_fails_after_the_upload_keeps_the_new_base()
    {
        api.Add("a.txt", content: "v1");
        var engine = Create();
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        api.AddUnnoticed("b.txt", "other"); // the cached listing doesn't know it, the move runs into it
        await SaveAsync(engine, "a.txt", "v2", close: false);

        var failure = await Assert.ThrowsAsync<RemoteException>(() => engine.RenameAsync(Root + "a.txt", Root + "b.txt", false, default));
        Assert.Equal(RemoteError.Conflict, failure.Error);
        await engine.CommitAsync(Root + "a.txt", default);

        Assert.Equal("v2", api.ContentOf("a.txt"));
        Assert.Equal("other", api.ContentOf("b.txt"));
        Assert.DoesNotContain(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_rename_with_upload_lets_other_files_go_on()
    {
        api.Add("report.docx", content: "v1");
        var engine = Create();
        await WriteNewAsync(engine, "~WRD0000.tmp", "v2");
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => { started.TrySetResult(); return hold.Task; };

        var save = engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", true, default);
        await started.Task;
        api.BeforeUpload = null;
        await engine.CreateAsync(Root + "other", true, default); // would wait for the upload if the gate were held
        Assert.False(save.IsCompleted);

        hold.SetResult();
        await save;
        Assert.Equal("v2", api.ContentOf("report.docx"));
    }

    [Fact]
    public async Task A_failed_copy_for_a_write_leaves_no_staging_file()
    {
        api.Add("plan.docx", content: "0123456789");
        var engine = Create();
        var entry = (await engine.GetEntryAsync(Root + "plan.docx", default))!;
        api.ServeOtherContent("plan.docx", Encoding.UTF8.GetBytes("short"));

        await Assert.ThrowsAsync<ContentChangedException>(() => engine.OpenForWriteAsync(entry, truncate: false, default));

        Assert.Empty(Directory.GetFiles(Staging));
    }

    [Fact]
    public async Task A_file_renamed_to_a_temp_name_after_its_close_uploads_only_when_still_there_minutes_later()
    {
        var engine = Create(background: true);
        api.FailUpload = _ => new RemoteException(RemoteError.Unavailable, "503");
        await WriteNewAsync(engine, "draft.txt", "x");
        await engine.WaitForUploadsAsync();
        Assert.Equal(1, engine.WaitingToUpload());

        await engine.RenameAsync(Root + "draft.txt", Root + "draft.tmp", false, default);
        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(4));

        Assert.Empty(api.Paths);
        Assert.Equal(0, engine.WaitingToUpload());
        Assert.Single(engine.Unsaved());
        Assert.NotNull(await engine.GetEntryAsync(Root + "draft.tmp", default));

        await DueAsync(engine, TimeSpan.FromMinutes(2));
        Assert.Equal("x", api.ContentOf("draft.tmp"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_new_file_renamed_to_a_temp_name_and_deleted_never_uploads()
    {
        var engine = Create(background: true);
        api.FailUpload = _ => new RemoteException(RemoteError.Unavailable, "503");
        await WriteNewAsync(engine, "draft.txt", "x");
        await engine.WaitForUploadsAsync();

        await engine.RenameAsync(Root + "draft.txt", Root + "draft.tmp", false, default);
        await engine.DeleteAsync(Root + "draft.tmp", default);
        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(10));

        Assert.Empty(api.Paths);
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task Deleting_a_renamed_away_original_that_was_written_to_keeps_the_saved_file()
    {
        var original = api.Add("report.docx", content: "v1");
        var engine = Create();

        // Word: the original steps aside, something writes to it there, the new content lands under the real name, the old one goes
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default);
        var aside = (await engine.GetEntryAsync(Root + "~WRL0001.tmp", default))!;
        await engine.OpenForWriteAsync(aside, truncate: false, default);
        engine.MarkDirty(aside);
        await WriteNewAsync(engine, "~WRD0000.tmp", "v2");
        await engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default);
        await engine.DeleteAsync(Root + "~WRL0001.tmp", default);
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal(0, api.Deletes);
        Assert.Equal(original.Id, api.ItemAt("report.docx")!.Id);
        Assert.Equal("v2", api.ContentOf("report.docx"));
    }

    /// <summary>A write through a handle, as the file system makes it: inside its lease.</summary>
    static void Append(DriveEngine engine, FsEntry handle, string text)
    {
        using var lease = engine.BeginWrite(handle) ?? throw new InvalidOperationException("not staged");
        File.AppendAllText(handle.StagingPath!, text);
        engine.MarkDirty(handle);
    }

    bool JournalSaysFlushed() => File.ReadAllText(Assert.Single(Directory.GetFiles(Staging, "*.json"))).Contains("\"Flushed\":true", StringComparison.Ordinal);

    [Fact]
    public async Task A_save_whose_answer_got_lost_is_found_and_never_becomes_a_conflict_copy()
    {
        var original = api.Add("notes.txt", content: "v1");
        var engine = Create();
        api.LoseAnswer = _ => new TaskCanceledException("no answer within 100 s");

        await SaveAsync(engine, "notes.txt", "v2");

        Assert.Equal("v2", api.ContentOf("notes.txt"));
        Assert.Equal(original.Id, api.ItemAt("notes.txt")!.Id);
        Assert.Single(api.Uploads);
        Assert.Equal(["notes.txt"], api.Paths);
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_save_whose_answer_got_lost_offline_is_looked_up_before_it_is_sent_again()
    {
        var original = api.Add("notes.txt", content: "v1");
        var engine = Create();
        api.LoseAnswer = _ => new HttpRequestException("connection reset");
        api.FailUploadedLookup = () => new HttpRequestException("No such host is known.");

        await SaveAsync(engine, "notes.txt", "v2");
        Assert.Equal(1, engine.WaitingToUpload());

        // Sent again with the eTag it had, it would get a 412 and end in a conflict copy of itself
        api.LoseAnswer = null;
        api.FailUploadedLookup = null;
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal("v2", api.ContentOf("notes.txt"));
        Assert.Equal(original.Id, api.ItemAt("notes.txt")!.Id);
        Assert.Single(api.Uploads);
        Assert.Equal(["notes.txt"], api.Paths);
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_close_while_another_handle_still_writes_only_flushes_and_the_last_close_uploads_both()
    {
        api.Add("data.bin", content: "");
        var engine = Create();
        var file = (await engine.GetEntryAsync(Root + "data.bin", default))!;
        var first = await engine.StageAsync(file, truncate: false, default);
        var second = await engine.StageAsync(file, truncate: false, default);

        Append(engine, first, "first ");
        await engine.CommitAsync(first, default);
        Assert.Empty(api.Uploads);
        Assert.True(JournalSaysFlushed());
        Assert.Equal(0, engine.WaitingToUpload());

        Append(engine, second, "second");
        await engine.CommitAsync(second, default);
        Assert.Equal("first second", api.ContentOf("data.bin"));
        Assert.Single(api.Uploads);
    }

    [Fact]
    public async Task A_file_another_writer_keeps_open_still_uploads_minutes_after_the_first_close()
    {
        api.Add("data.bin", content: "");
        var engine = Create();
        var file = (await engine.GetEntryAsync(Root + "data.bin", default))!;
        var first = await engine.StageAsync(file, truncate: false, default);
        await engine.StageAsync(file, truncate: false, default);
        Append(engine, first, "x");
        await engine.CommitAsync(first, default);

        await DueAsync(engine, TimeSpan.FromMinutes(5));
        Assert.Empty(api.Uploads);
        await DueAsync(engine, TimeSpan.FromMinutes(6));
        Assert.Equal("x", api.ContentOf("data.bin"));
    }

    [Fact]
    public async Task A_close_commits_only_what_its_handle_wrote()
    {
        api.Add("log.txt", content: "v1");
        var engine = Create();
        var first = await engine.StageAsync((await engine.GetEntryAsync(Root + "log.txt", default))!, truncate: true, default);
        Append(engine, first, "v2");
        engine.CommitLater(first); // mapped writes: uploads once they go quiet
        await DueAsync(engine, TimeSpan.FromSeconds(5));
        Assert.Equal("v2", api.ContentOf("log.txt"));

        // Another handle is still writing when the first one closes: its half-written content isn't uploaded by that close
        var second = await engine.StageAsync((await engine.GetEntryAsync(Root + "log.txt", default))!, truncate: false, default);
        Append(engine, second, " partial");
        await engine.CommitAsync(first, default);
        Assert.Equal("v2", api.ContentOf("log.txt"));

        Append(engine, second, " done");
        await engine.CommitAsync(second, default);
        Assert.Equal("v2 partial done", api.ContentOf("log.txt"));
        Assert.Equal(2, api.Uploads.Count);
    }

    [Fact]
    public async Task A_delete_never_takes_a_colleagues_newer_version_along()
    {
        api.Add("plan.txt", content: "v1");
        var engine = Create();
        await engine.DeleteAsync(Root + "plan.txt", default);
        api.RemoteEdit("plan.txt", "theirs"); // saved elsewhere within the settle window

        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal("theirs", api.ContentOf("plan.txt"));
        Assert.Equal(0, api.Deletes);
        Assert.Equal(api.ItemAt("plan.txt")!.CTag, (await engine.GetEntryAsync(Root + "plan.txt", default))!.ContentTag);
        Assert.Equal("plan.txt", Assert.Single(notices, n => n.Kind == NoticeKind.NotDeleted).Name);
    }

    [Fact]
    public async Task A_delete_goes_ahead_when_only_metadata_changed_meanwhile()
    {
        var original = api.Add("plan.txt", content: "v1");
        var engine = Create();
        await engine.DeleteAsync(Root + "plan.txt", default);
        api.RemoteMetadataEdit("plan.txt"); // a column, a label: a new eTag, the same content

        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Empty(api.Paths);
        Assert.Equal(2, api.DeleteIfMatches.Count); // refused (412) for the eTag the user saw, then sent with the eTag now
        Assert.Equal(original.ETag, api.DeleteIfMatches[0]);
        Assert.NotEqual(original.ETag, api.DeleteIfMatches[1]);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task Finished_background_work_is_not_kept()
    {
        var engine = Create(background: true);
        api.Add("old.txt");
        await engine.DeleteAsync(Root + "old.txt", default);
        for (var i = 0; i < 20; i++)
        {
            await WriteNewAsync(engine, $"f{i}.txt", "x");
        }
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        for (var waited = 0; (Count(engine, "backgroundUploads") > 0 || Count(engine, "backgroundWork") > 0) && waited < 2000; waited += 20)
        {
            await Task.Delay(20);
        }
        Assert.Equal(0, Count(engine, "backgroundUploads"));
        Assert.Equal(0, Count(engine, "backgroundWork"));
        Assert.Equal(20, api.Uploads.Count);
    }

    /// <summary>Entries of one of the engine's own collections (no public view of it), under the lock the engine takes.</summary>
    static int Count(DriveEngine engine, string field)
    {
        var collection = typeof(DriveEngine).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(engine)!;
        lock (collection)
        {
            return (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;
        }
    }

    [Fact]
    public async Task A_close_of_a_file_renamed_away_with_changes_is_on_disk_before_it_returns()
    {
        api.Add("notes.txt", content: "v1");
        var engine = Create();
        var handle = await SaveAsync(engine, "notes.txt", "v2", close: false);
        await engine.RenameAsync(Root + "notes.txt", Root + "~notes.tmp", false, default); // renamed away: its upload waits for the settle window

        await engine.CommitAsync(handle, default);

        Assert.True(JournalSaysFlushed());
        Assert.Empty(api.Uploads);
    }

    [Fact]
    public async Task An_original_renamed_to_a_real_name_after_a_new_file_took_over_its_item_never_moves_the_item()
    {
        // The original steps aside, a new file is made under its name (taking over its item), and the original becomes the backup
        var original = api.Add("drawing.dwg", content: "v1");
        var engine = Create();
        Assert.Equal("v1", await ReadAllAsync(engine, (await engine.GetEntryAsync(Root + "drawing.dwg", default))!));
        await engine.RenameAsync(Root + "drawing.dwg", Root + "~drawing.tmp", false, default);
        var saved = await WriteNewAsync(engine, "drawing.dwg", "v2", close: false);
        await engine.RenameAsync(Root + "~drawing.tmp", Root + "drawing.bak", false, default);
        await engine.CommitAsync(saved, default);
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        Assert.Equal(original.Id, api.ItemAt("drawing.dwg")!.Id);
        Assert.Equal("v2", api.ContentOf("drawing.dwg"));
        Assert.Equal("v1", api.ContentOf("drawing.bak"));
        Assert.Equal(["drawing.bak", "drawing.dwg"], api.Paths);
    }

    [Fact]
    public async Task A_create_at_a_name_waiting_to_be_deleted_sends_that_delete_at_once_in_the_foreground()
    {
        for (var i = 0; i < 8; i++)
        {
            api.Add($"f{i}.txt", content: $"{i}");
        }
        var engine = Create();
        for (var i = 0; i < 8; i++)
        {
            await engine.DeleteAsync(Root + $"f{i}.txt", default);
        }
        // Background requests held, as for minutes after throttling; promoted ones go
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeDelete = async () =>
        {
            if (RequestPriority.Current is { IsBackground: true } priority)
            {
                await Task.WhenAny(paused.Task, priority.Promoted);
            }
        };
        now += TimeSpan.FromMinutes(1);
        await engine.ProcessDueAsync(false, default);

        for (var i = 0; i < 8; i++)
        {
            Assert.Null((await engine.CreateAsync(Root + $"f{i}.txt", false, default).WaitAsync(TimeSpan.FromSeconds(10))).ItemId);
        }
        Assert.Equal(8, api.Deletes);
        paused.SetResult();
        await engine.WaitForUploadsAsync();
    }

    /// <summary>True for each operation that went through, false for each refused with a name collision.</summary>
    static async Task<bool[]> OutcomesAsync(params Task[] operations)
    {
        var outcomes = new bool[operations.Length];
        for (var i = 0; i < operations.Length; i++)
        {
            try
            {
                await operations[i];
                outcomes[i] = true;
            }
            catch (FsException e) when (e.Error == FsError.NameCollision)
            {
            }
        }
        return outcomes;
    }

    [Fact]
    public async Task A_create_and_a_rename_onto_one_name_at_once_leave_one_file_there()
    {
        for (var i = 0; i < 20; i++)
        {
            api.Add($"src{i}.txt", content: "moved");
        }
        var engine = Create(background: true);
        for (var i = 0; i < 20; i++)
        {
            var create = Task.Run(() => engine.CreateAsync(Root + $"dst{i}.txt", false, default));
            var rename = Task.Run(() => engine.RenameAsync(Root + $"src{i}.txt", Root + $"dst{i}.txt", false, default));
            var outcomes = await OutcomesAsync(create, rename);
            Assert.Single(outcomes, won => won);
            if (outcomes[0])
            {
                await engine.CommitAsync(await create, default);
            }
        }
        await engine.WaitForUploadsAsync();

        for (var i = 0; i < 20; i++)
        {
            var moved = api.ContentOf($"dst{i}.txt") == "moved";
            Assert.True(moved ? api.ItemAt($"src{i}.txt") is null : api.ContentOf($"dst{i}.txt") == "" && api.ItemAt($"src{i}.txt") is not null);
        }
        Assert.DoesNotContain(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Two_renames_onto_one_name_at_once_leave_both_files()
    {
        // Files on the server, and new ones whose uploads are still running
        for (var i = 0; i < 20; i += 2)
        {
            api.Add($"a{i}.txt", content: "a");
            api.Add($"b{i}.txt", content: "b");
        }
        var engine = Create(background: true);
        var network = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => network.Task;
        for (var i = 0; i < 20; i++)
        {
            if (i % 2 == 1)
            {
                await WriteNewAsync(engine, $"a{i}.txt", "a");
                await WriteNewAsync(engine, $"b{i}.txt", "b");
            }
            var outcomes = await OutcomesAsync(
                Task.Run(() => engine.RenameAsync(Root + $"a{i}.txt", Root + $"c{i}.txt", false, default)),
                Task.Run(() => engine.RenameAsync(Root + $"b{i}.txt", Root + $"c{i}.txt", false, default)));
            Assert.Single(outcomes, won => won);
        }
        network.SetResult();
        await engine.WaitForUploadsAsync();

        for (var i = 0; i < 20; i++)
        {
            Assert.NotNull(api.ItemAt($"c{i}.txt"));
            Assert.Single(new[] { $"a{i}.txt", $"b{i}.txt" }, p => api.ItemAt(p) is not null);
        }
        Assert.Equal(40, api.Paths.Count());
    }

    [Fact]
    public async Task A_delete_and_a_create_at_one_name_at_once_never_lose_the_new_file()
    {
        var originals = Enumerable.Range(0, 20).Select(i => api.Add($"x{i}.txt", content: "old")).ToArray();
        var engine = Create();
        var created = new bool[20];
        for (var i = 0; i < 20; i++)
        {
            var name = $"x{i}.txt";
            var delete = Task.Run(() => engine.DeleteAsync(Root + name, default));
            var create = Task.Run(async () =>
            {
                var entry = await engine.CreateAsync(Root + name, false, default);
                Append(engine, entry, "new");
                await engine.CommitAsync(entry, default);
            });
            var outcomes = await OutcomesAsync(delete, create);
            Assert.True(outcomes[0]);
            created[i] = outcomes[1];
        }
        await DueAsync(engine, TimeSpan.FromMinutes(1));

        for (var i = 0; i < 20; i++)
        {
            // Made after the delete, the new file took over the item; refused before it, the delete went ahead
            Assert.Equal(created[i] ? "new" : null, api.ContentOf($"x{i}.txt"));
            Assert.Equal(created[i] ? originals[i].Id : null, api.ItemAt($"x{i}.txt")?.Id);
        }
    }

    [Fact]
    public async Task A_new_file_at_the_old_name_of_one_that_moved_while_it_uploads_waits_for_that_upload()
    {
        var engine = Create(background: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => { started.TrySetResult(); return hold.Task; };
        await WriteNewAsync(engine, "report.pdf", "first");
        await started.Task;
        await engine.RenameAsync(Root + "report.pdf", Root + "first.pdf", false, default);
        api.BeforeUpload = null;

        // The first file is up under that name until it moves: a new one there would collide with it
        var create = WriteNewAsync(engine, "report.pdf", "second");
        await Task.Delay(100);
        Assert.False(create.IsCompleted);
        hold.SetResult();
        await create;
        await engine.WaitForUploadsAsync();

        Assert.Equal(["first.pdf", "report.pdf"], api.Paths);
        Assert.Equal("first", api.ContentOf("first.pdf"));
        Assert.Equal("second", api.ContentOf("report.pdf"));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
