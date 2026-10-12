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
        await SaveAsync(engine, "notes.txt", "mine", close: false);
        api.RemoteEdit("notes.txt", "them");

        var restarted = Create();
        restarted.Recover();
        await DueAsync(restarted, TimeSpan.Zero);

        Assert.Equal("them", api.ContentOf("notes.txt"));
        Assert.Equal("mine", api.ContentOf(Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal))));
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
    public async Task A_file_renamed_to_a_temp_name_after_its_close_never_uploads()
    {
        var engine = Create(background: true);
        api.FailUpload = _ => new RemoteException(RemoteError.Unavailable, "503");
        await WriteNewAsync(engine, "draft.txt", "x");
        await engine.WaitForUploadsAsync();
        Assert.Equal(1, engine.WaitingToUpload());

        await engine.RenameAsync(Root + "draft.txt", Root + "draft.tmp", false, default);
        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(5));

        Assert.Empty(api.Paths);
        Assert.Equal(0, engine.WaitingToUpload());
        Assert.NotNull(await engine.GetEntryAsync(Root + "draft.tmp", default));
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
