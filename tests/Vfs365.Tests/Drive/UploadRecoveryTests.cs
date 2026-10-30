using System.Globalization;
using Vfs365.Agent;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Xunit.Abstractions;

namespace Vfs365.Tests.Drive;

/// <summary>
/// Uploads that can't go where the file was saved: a folder renamed, moved or deleted in Microsoft 365 before the upload, a folder the
/// user may not add files to. Nothing is lost, nothing is retried for ever.
/// </summary>
public sealed class UploadRecoveryTests(ITestOutputHelper output) : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly List<string> log = [];
    readonly List<EngineNotice> notices = [];
    DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";

    DriveEngine Create(bool background = false) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                UploadNewFilesInBackground = background,
                Log = line => { lock (log) { log.Add(line); } },
                Notice = notice => { lock (notices) { notices.Add(notice); } },
            });

    /// <summary>An engine with the change feed running and these folders listed (as Explorer showed them).</summary>
    async Task<DriveEngine> ListedAsync(bool background, params string[] folders)
    {
        var engine = Create(background);
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        foreach (var folder in folders)
        {
            await engine.ListAsync((await engine.GetEntryAsync(Root + folder, default))!, default);
        }
        return engine;
    }

    static async Task<FsEntry> WriteNewAsync(DriveEngine engine, string path, string content)
    {
        var entry = await engine.CreateAsync(Root + path, false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + path, default);
        await engine.WaitForUploadsAsync();
        return entry;
    }

    async Task DueAsync(DriveEngine engine, TimeSpan later)
    {
        now += later;
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
    }

    async Task<IReadOnlyList<FsEntry>> ListAsync(DriveEngine engine, string folder) =>
        await engine.ListAsync((await engine.GetEntryAsync(Root + folder, default))!, default);

    static FileState? StateOf(DriveEngine engine, string path) =>
        engine.FileStates().Where(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Select(s => (FileState?)s.State).FirstOrDefault();

    static readonly Exception Offline = new HttpRequestException("No such host is known. (contoso.sharepoint.com:443)");

    void Dump()
    {
        lock (log)
        {
            foreach (var line in log)
            {
                output.WriteLine(line);
            }
        }
        output.WriteLine("server: " + string.Join(", ", api.Paths));
    }

    [Fact]
    public async Task A_new_file_whose_folder_was_renamed_before_it_uploaded_follows_the_folder()
    {
        api.Add("Project", folder: true);
        var engine = await ListedAsync(true, "Project");

        // Offline: the new file is journaled and waits
        api.FailUpload = _ => Offline;
        await WriteNewAsync(engine, "Project\\notes.txt", "offline work");

        // A colleague renames the folder; the feed applies it when the network is back
        api.RemoteMove("Project", "Project 2026");
        await engine.PollAsync("b!me");
        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal("offline work", api.ContentOf("Project 2026/notes.txt"));
        Assert.Empty(engine.Unsaved());
        Assert.Contains(await ListAsync(engine, "Project 2026"), e => e.Name == "notes.txt");
        Assert.DoesNotContain(notices, n => n.Kind == NoticeKind.ConflictCopy);
    }

    [Fact]
    public async Task A_file_waiting_long_follows_its_folder_as_soon_as_the_change_feed_shows_the_rename()
    {
        api.Add("Project", folder: true);
        var engine = await ListedAsync(true, "Project");
        api.FailUpload = _ => Offline;
        await WriteNewAsync(engine, "Project\\notes.txt", "offline work");
        for (var i = 0; i < 8; i++)
        {
            await DueAsync(engine, TimeSpan.FromMinutes(16)); // backed off to 15 minutes
        }

        api.FailUpload = null;
        api.RemoteMove("Project", "Project 2026");
        await engine.PollAsync("b!me");
        await DueAsync(engine, TimeSpan.FromSeconds(1));
        Dump();

        Assert.Equal("offline work", api.ContentOf("Project 2026/notes.txt"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_file_whose_folder_is_gone_while_still_offline_tries_once_then_backs_off()
    {
        api.Add("Project", folder: true);
        var engine = await ListedAsync(true, "Project");
        var attempts = 0;
        api.FailUpload = _ => { Interlocked.Increment(ref attempts); return Offline; };
        await WriteNewAsync(engine, "Project\\notes.txt", "offline work");
        for (var i = 0; i < 8; i++)
        {
            await DueAsync(engine, TimeSpan.FromMinutes(16));
        }
        var before = attempts;

        api.RemoteDelete("Project");
        await engine.PollAsync("b!me");
        for (var i = 0; i < 10; i++)
        {
            await DueAsync(engine, TimeSpan.FromSeconds(1));
        }
        Dump();

        Assert.Equal(before + 1, attempts); // the recovered copy, once
        Assert.Single(engine.Unsaved());
    }

    [Fact]
    public async Task A_waiting_file_shows_in_its_folders_new_place_and_its_neighbours_move_along()
    {
        api.Add("Project", folder: true);
        api.Add("Project/Sub", folder: true);
        var engine = await ListedAsync(true, "Project", "Project\\Sub");
        api.FailUpload = _ => Offline;
        await WriteNewAsync(engine, "Project\\a.txt", "a");
        await WriteNewAsync(engine, "Project\\Sub\\b.txt", "b");

        api.RemoteMove("Project", "Project 2026");
        await engine.PollAsync("b!me");
        await DueAsync(engine, TimeSpan.FromMinutes(1)); // still failing: they wait where the folder is now
        Dump();

        Assert.Null(await engine.GetEntryAsync(Root + "Project\\a.txt", default));
        Assert.Contains(await ListAsync(engine, "Project 2026"), e => e.Name == "a.txt");
        Assert.NotNull(await engine.GetEntryAsync(Root + "Project 2026\\Sub\\b.txt", default));
        Assert.Equal(2, engine.Unsaved().Count);

        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(16));
        Assert.Equal("a", api.ContentOf("Project 2026/a.txt"));
        Assert.Equal("b", api.ContentOf("Project 2026/Sub/b.txt"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_new_file_waiting_across_a_restart_follows_its_folder_renamed_meanwhile()
    {
        api.Add("Project", folder: true);
        var engine = await ListedAsync(true, "Project");
        api.FailUpload = _ => Offline;
        await WriteNewAsync(engine, "Project\\notes.txt", "offline work");

        api.RemoteMove("Project", "Project 2026");
        api.FailUpload = null;
        var restarted = Create(background: true);
        Assert.Equal(1, restarted.Recover());
        await restarted.ProcessDueAsync(false, default);
        await restarted.WaitForUploadsAsync();
        Dump();

        Assert.Equal("offline work", api.ContentOf("Project 2026/notes.txt"));
        Assert.Empty(restarted.Unsaved());
    }

    [Fact]
    public async Task An_edit_of_a_file_whose_folder_was_deleted_is_saved_as_a_recovered_copy_above_it()
    {
        api.Add("Project", folder: true);
        api.Add("Project/plan.txt", content: "v1");
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync(Root + "Project", default))!, default);

        var entry = await engine.StageAsync((await engine.GetEntryAsync(Root + "Project\\plan.txt", default))!, truncate: true, default);
        await File.WriteAllTextAsync(entry.StagingPath!, "a day of work");
        engine.MarkDirty(entry);
        api.RemoteDelete("Project");
        await engine.PollAsync("b!me");
        await engine.CommitAsync(Root + "Project\\plan.txt", default);
        Dump();

        var copy = Assert.Single(api.Paths);
        Assert.Matches(@"^plan \(recovered \d{4}-\d{2}-\d{2} \d{6}\)\.txt$", copy);
        Assert.Equal("a day of work", api.ContentOf(copy));
        Assert.Empty(engine.Unsaved());
        var notice = Assert.Single(notices, n => n.Kind == NoticeKind.ConflictCopy);
        Assert.Equal(("plan.txt", Root + copy), (notice.Name, notice.Detail));
    }

    [Fact]
    public async Task A_new_file_whose_folder_was_deleted_is_saved_as_a_recovered_copy_in_the_nearest_folder_still_there()
    {
        api.Add("Projects", folder: true);
        api.Add("Projects/Alpha", folder: true);
        var engine = await ListedAsync(true, "Projects", "Projects\\Alpha");
        api.FailUpload = _ => Offline;
        await WriteNewAsync(engine, "Projects\\Alpha\\notes.txt", "offline work");

        api.RemoteDelete("Projects/Alpha");
        await engine.PollAsync("b!me");
        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        var copy = Assert.Single(api.Paths, p => p.StartsWith("Projects/notes (recovered ", StringComparison.Ordinal));
        Assert.Equal("offline work", api.ContentOf(copy));
        Assert.Empty(engine.Unsaved());
        Assert.Contains(await ListAsync(engine, "Projects"), e => e.Name == copy["Projects/".Length..]);
        var notice = Assert.Single(notices, n => n.Kind == NoticeKind.ConflictCopy);
        Assert.Equal(Root + copy.Replace('/', '\\'), notice.Detail);
        Assert.Equal(Root + "Projects\\Alpha\\notes.txt", notice.Path);
    }

    [Fact]
    public async Task A_file_with_nowhere_to_go_stays_unsaved_without_retrying_on_a_timer()
    {
        api.Add("Projects", folder: true);
        api.Add("Projects/Alpha", folder: true);
        var engine = await ListedAsync(true, "Projects", "Projects\\Alpha");
        api.FailUpload = _ => Offline;
        await WriteNewAsync(engine, "Projects\\Alpha\\notes.txt", "offline work");

        api.RemoteDelete("Projects/Alpha");
        await engine.PollAsync("b!me");
        var attempts = 0;
        api.FailUpload = _ => { Interlocked.Increment(ref attempts); return new RemoteException(RemoteError.AccessDenied, "403 accessDenied"); };
        for (var i = 0; i < 24 * 4; i++) // a day
        {
            await DueAsync(engine, TimeSpan.FromMinutes(15));
        }
        Dump();

        Assert.Equal(1, attempts);
        Assert.Single(engine.Unsaved());
        Assert.Equal(0, engine.WaitingToUpload());
        Assert.Equal(FileState.Problem, StateOf(engine, Root + "Projects\\Alpha\\notes.txt"));
        var waiting = notices.Where(n => n.Kind == NoticeKind.UploadWaiting).ToList();
        Assert.Contains(waiting, n => n.Detail!.StartsWith("404 its folder Projects/Alpha", StringComparison.Ordinal));
        Assert.Equal(Cause.Gone, PlainCause.Of(waiting[^1].Detail));
    }

    [Fact]
    public async Task A_new_file_refused_with_403_waits_for_the_next_save_rename_or_start_and_shows_a_problem()
    {
        api.Add("Approved", folder: true);
        var engine = await ListedAsync(true, "Approved");
        var attempts = 0;
        api.FailUpload = _ => { Interlocked.Increment(ref attempts); return new RemoteException(RemoteError.AccessDenied, "403 accessDenied"); };

        await WriteNewAsync(engine, "Approved\\copy.pdf", "pdf"); // the close succeeds at once
        for (var i = 0; i < 24 * 4; i++) // a day
        {
            await DueAsync(engine, TimeSpan.FromMinutes(15));
        }
        Dump();

        Assert.Equal(1, attempts);
        Assert.Single(engine.Unsaved());
        Assert.Equal(0, engine.WaitingToUpload());
        Assert.Single(notices, n => n.Kind == NoticeKind.UploadWaiting);
        Assert.Equal(FileState.Problem, StateOf(engine, Root + "Approved\\copy.pdf"));

        // Saved again: tried again
        var staged = await engine.StageAsync((await engine.GetEntryAsync(Root + "Approved\\copy.pdf", default))!, truncate: false, default);
        await File.AppendAllTextAsync(staged.StagingPath!, " v2");
        engine.MarkDirty(staged);
        await engine.CommitAsync(staged, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal(2, attempts);

        // Renamed: tried again
        await engine.RenameAsync(Root + "Approved\\copy.pdf", Root + "Approved\\copy 2.pdf", false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal(3, attempts);
        await DueAsync(engine, TimeSpan.FromHours(1));
        Assert.Equal(3, attempts);

        // The agent starts again: tried again, and uploads once allowed
        api.FailUpload = null;
        var restarted = Create(background: true);
        Assert.Equal(1, restarted.Recover());
        await restarted.ProcessDueAsync(false, default);
        await restarted.WaitForUploadsAsync();
        Assert.Equal("pdf v2", api.ContentOf("Approved/copy 2.pdf"));
        Assert.Empty(restarted.Unsaved());
    }

    [Fact]
    public async Task An_edit_refused_with_403_is_still_retried()
    {
        api.Add("notes.txt", content: "v1");
        var engine = await ListedAsync(false);
        api.FailUpload = _ => new RemoteException(RemoteError.AccessDenied, "403 accessDenied");
        var staged = await engine.StageAsync((await engine.GetEntryAsync(Root + "notes.txt", default))!, truncate: true, default);
        await File.WriteAllTextAsync(staged.StagingPath!, "v2");
        engine.MarkDirty(staged);
        await engine.CommitAsync(staged, default);
        Assert.Equal(1, engine.WaitingToUpload());

        api.FailUpload = null;
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Assert.Equal("v2", api.ContentOf("notes.txt"));
    }

    [Fact]
    public void Notices_say_where_a_copy_went_and_that_a_refused_file_waits_for_the_user()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        var t0 = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var batcher = new NoticeBatcher();
        batcher.Add(new EngineNotice(NoticeKind.ConflictCopy, "notes.txt", Root + "Project\\notes.txt", Root + "notes (recovered 2026-10-30 120000).txt"),
            Cause.Other, t0);
        batcher.Add(new EngineNotice(NoticeKind.UploadWaiting, "copy.pdf", Root + "Approved\\copy.pdf", "403 Forbidden from https://graph.microsoft.com/v1.0/x: accessDenied"),
            Cause.NoAccess, t0);

        var toasts = batcher.Due(t0 + NoticeBatcher.Window, english);

        Assert.Contains(("Saved as a copy",
            @"The folder of notes.txt was moved or deleted in Microsoft 365. Your version is saved as \OneDrive\notes (recovered 2026-10-30 120000).txt."), toasts);
        Assert.Contains(("Waiting to upload", "copy.pdf isn't uploaded: no access. It is kept on this PC until you save, rename or move it again."), toasts);
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
