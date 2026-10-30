using System.Text;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Xunit.Abstractions;

namespace Vfs365.Tests.Drive;

/// <summary>
/// Rename-based saves (Word, Excel, replace-style editors) name the version their app had open, as writes in place do: a colleague's
/// newer version the change feed already showed is never replaced unseen.
/// </summary>
public sealed class RenameSaveConflictTests(ITestOutputHelper output) : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly List<string> log = [];
    readonly List<EngineNotice> notices = [];
    DateTimeOffset now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";

    // Openers as the front end tells them apart (a process ID there)
    const uint Word = 4242, Editor = 5150, Preview = 6060, Excel = 7070;

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

    async Task<DriveEngine> ListedAsync(bool background = false)
    {
        var engine = Create(background);
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        return engine;
    }

    static async Task WriteNewAsync(DriveEngine engine, string name, string content)
    {
        var entry = await engine.CreateAsync(Root + name, false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + name, default);
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

    /// <summary>An app opens the file and reads it, keeping the handle open (as Word keeps its document).</summary>
    static async Task<IDisposable?> OpenAsync(DriveEngine engine, string name, uint opener, string expected)
    {
        var entry = (await engine.GetEntryAsync(Root + name, default))!;
        var handle = engine.Opened(entry, opener);
        Assert.Equal(expected, await ReadAllAsync(engine, entry));
        return handle;
    }

    /// <summary>A colleague saves in the browser; the change feed applies it.</summary>
    async Task ColleagueSavesAsync(DriveEngine engine, string name, string content)
    {
        now += TimeSpan.FromMinutes(5);
        api.RemoteEdit(name, content, "Colleague", now);
        await engine.PollAsync("b!me");
    }

    async Task WordSaveAsync(DriveEngine engine, string content)
    {
        await WriteNewAsync(engine, "~WRD0000.tmp", content);
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default, Word);
        await engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default, Word);
        await engine.DeleteAsync(Root + "~WRL0001.tmp", default, Word);
    }

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
    public async Task A_Word_save_over_a_colleagues_newer_version_fails_and_keeps_both()
    {
        var original = api.Add("report.docx", content: "v1");
        var engine = await ListedAsync();
        using var word = await OpenAsync(engine, "report.docx", Word, "v1");
        await ColleagueSavesAsync(engine, "report.docx", "colleague");

        // Word saves its v1-based document: temp file, original renamed away, temp renamed over it
        await WriteNewAsync(engine, "~WRD0000.tmp", "v1 + mine");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default, Word);
        var refused = await Assert.ThrowsAsync<RemoteException>(() => engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default, Word));

        // Word's own recovery: its original back under its name; its temp file went into the copy already
        await engine.RenameAsync(Root + "~WRL0001.tmp", Root + "report.docx", false, default, Word);
        await Assert.ThrowsAsync<FsException>(() => engine.DeleteAsync(Root + "~WRD0000.tmp", default, Word));
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal(RemoteError.Conflict, refused.Error);
        Assert.Equal("colleague", api.ContentOf("report.docx"));
        Assert.DoesNotContain(api.Uploads, u => u.ItemId == original.Id);
        var copy = Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal("v1 + mine", api.ContentOf(copy));
        var notice = Assert.Single(notices, n => n.Kind == NoticeKind.ConflictCopy);
        Assert.Equal(("report.docx", copy, "Colleague"), (notice.Name, notice.Detail, notice.By));
        Assert.Equal("colleague", await ReadAllAsync(engine, (await engine.GetEntryAsync(Root + "report.docx", default))!));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_replace_save_over_a_colleagues_newer_version_fails_and_the_editors_file_stays()
    {
        var original = api.Add("notes.txt", content: "v1");
        var engine = await ListedAsync();
        using var editor = await OpenAsync(engine, "notes.txt", Editor, "v1");
        await ColleagueSavesAsync(engine, "notes.txt", "colleague");

        // Editor pattern: write notes.txt.new, MoveFileEx(REPLACE_EXISTING) onto notes.txt
        await WriteNewAsync(engine, "notes.txt.new", "v1 + mine");
        var refused = await Assert.ThrowsAsync<RemoteException>(() => engine.RenameAsync(Root + "notes.txt.new", Root + "notes.txt", true, default, Editor));
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal(RemoteError.Conflict, refused.Error);
        Assert.Equal("colleague", api.ContentOf("notes.txt"));
        Assert.Equal("v1 + mine", api.ContentOf("notes.txt.new"));
        Assert.DoesNotContain(api.Uploads, u => u.ItemId == original.Id);
    }

    [Fact]
    public async Task A_replace_save_of_a_file_not_uploaded_yet_fails_and_the_file_still_uploads()
    {
        api.Add("notes.txt", content: "v1");
        var engine = await ListedAsync(background: true);
        using var editor = await OpenAsync(engine, "notes.txt", Editor, "v1");
        await ColleagueSavesAsync(engine, "notes.txt", "colleague");

        api.FailUpload = _ => new HttpRequestException("offline");
        await WriteNewAsync(engine, "notes.txt.new", "v1 + mine");
        await engine.WaitForUploadsAsync();
        api.FailUpload = null;
        await Assert.ThrowsAsync<RemoteException>(() => engine.RenameAsync(Root + "notes.txt.new", Root + "notes.txt", true, default, Editor));
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal("colleague", api.ContentOf("notes.txt"));
        Assert.Equal("v1 + mine", api.ContentOf("notes.txt.new"));
        Assert.DoesNotContain(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Excel_save_that_deletes_first_neither_replaces_nor_deletes_a_colleagues_newer_version()
    {
        api.Add("book.xlsx", content: "v1");
        var engine = await ListedAsync();
        var excel = await OpenAsync(engine, "book.xlsx", Excel, "v1");
        await ColleagueSavesAsync(engine, "book.xlsx", "colleague");

        await WriteNewAsync(engine, "8A3F1C2D", "v1 + mine");
        await engine.DeleteAsync(Root + "book.xlsx", default, Excel); // its document handle goes with the delete
        excel?.Dispose();
        await Assert.ThrowsAsync<RemoteException>(() => engine.RenameAsync(Root + "8A3F1C2D", Root + "book.xlsx", false, default, Excel));
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal("colleague", api.ContentOf("book.xlsx"));
        var copy = Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal("v1 + mine", api.ContentOf(copy));
        Assert.Equal(0, api.Deletes);
        Assert.NotNull(await engine.GetEntryAsync(Root + "book.xlsx", default));
    }

    [Fact]
    public async Task A_Word_save_of_the_version_it_opened_goes_into_the_item_also_twice_with_the_document_open()
    {
        var original = api.Add("report.docx", content: "v1");
        var engine = await ListedAsync();
        using var word = await OpenAsync(engine, "report.docx", Word, "v1");

        await WordSaveAsync(engine, "v2");
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        await WordSaveAsync(engine, "v3"); // the handle on v1 now counts as on this engine's save
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal("v3", api.ContentOf("report.docx"));
        Assert.Equal(original.Id, api.ItemAt("report.docx")!.Id);
        Assert.Equal(["report.docx"], api.Paths);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task A_handle_that_registers_the_version_its_apps_own_save_replaced_doesnt_refuse_the_next_save()
    {
        var original = api.Add("report.docx", content: "v1");
        var engine = await ListedAsync();
        var looked = (await engine.GetEntryAsync(Root + "report.docx", default))!; // an open looks up v1...

        await WordSaveAsync(engine, "v2"); // ...the app's own save replaces it meanwhile...
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        using var late = engine.Opened(looked, Word); // ...and the open registers v1 after that save moved the other handles along
        await WordSaveAsync(engine, "v3");
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal("v3", api.ContentOf("report.docx"));
        Assert.Equal(original.Id, api.ItemAt("report.docx")!.Id);
        Assert.Equal(["report.docx"], api.Paths);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task Another_apps_handle_on_an_older_version_doesnt_refuse_a_save_of_the_current_one()
    {
        api.Add("report.docx", content: "v1");
        var engine = await ListedAsync();
        using var preview = await OpenAsync(engine, "report.docx", Preview, "v1"); // Explorer's preview keeps v1 open
        await ColleagueSavesAsync(engine, "report.docx", "colleague");
        using var word = await OpenAsync(engine, "report.docx", Word, "colleague");

        await WordSaveAsync(engine, "colleague + mine");
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal("colleague + mine", api.ContentOf("report.docx"));
        Assert.Equal(["report.docx"], api.Paths);
    }

    [Fact]
    public async Task A_Word_save_after_its_document_handle_closed_still_names_the_version_it_had_when_renaming_away()
    {
        api.Add("report.docx", content: "v1");
        var engine = await ListedAsync();
        var word = await OpenAsync(engine, "report.docx", Word, "v1");
        await ColleagueSavesAsync(engine, "report.docx", "colleague");

        await WriteNewAsync(engine, "~WRD0000.tmp", "v1 + mine");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default, Word);
        word?.Dispose();
        await Assert.ThrowsAsync<RemoteException>(() => engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default, Word));

        // Word deletes the renamed-away original instead of restoring it: the colleague's version stays
        await engine.DeleteAsync(Root + "~WRL0001.tmp", default, Word);
        await DueAsync(engine, TimeSpan.FromMinutes(1));
        Dump();

        Assert.Equal("colleague", api.ContentOf("report.docx"));
        Assert.Equal("v1 + mine", api.ContentOf(Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal))));
        Assert.Equal(0, api.Deletes);
    }

    [Fact]
    public async Task A_save_whose_conflict_copy_cant_upload_fails_and_keeps_the_temp_file()
    {
        api.Add("report.docx", content: "v1");
        var engine = await ListedAsync();
        using var word = await OpenAsync(engine, "report.docx", Word, "v1");
        await ColleagueSavesAsync(engine, "report.docx", "colleague");

        await WriteNewAsync(engine, "~WRD0000.tmp", "v1 + mine");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default, Word);
        api.FailUpload = target => target.ItemId is null ? new HttpRequestException("offline") : null;
        await Assert.ThrowsAsync<HttpRequestException>(() => engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default, Word));

        Assert.Equal("v1 + mine", await ReadAllAsync(engine, (await engine.GetEntryAsync(Root + "~WRD0000.tmp", default))!));
        Assert.Equal("colleague", api.ContentOf("report.docx"));
        Assert.Empty(notices);
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
