using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Application save patterns must land as one new version of the same item: same ID, nothing deleted, no temp files.</summary>
public sealed class SaveFidelityTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly DriveEngine engine;
    DateTimeOffset now = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    public SaveFidelityTests()
    {
        var ns = DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"),
            [new LibraryEntry("k", "s", "w", "l", "https://x.sharepoint.com/sites/Finance", "Finance", "Documents", false, 1)],
            (_, _) => Task.FromResult("b!finance"));
        engine = new DriveEngine(ns, api, new ContentCache(Path.Combine(directory, "cache"), api), new DriveEngineOptions
        {
            StagingDirectory = Path.Combine(directory, "staging"),
            Clock = () => now,
        });
    }

    const string Root = "\\OneDrive\\";

    async Task WriteNewAsync(string name, string content)
    {
        var entry = await engine.CreateAsync(Root + name, false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + name, default);
    }

    async Task OverwriteAsync(string name, string content)
    {
        var entry = (await engine.GetEntryAsync(Root + name, default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: true, default);
        await File.WriteAllTextAsync(staging, content);
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + name, default);
    }

    async Task SettleAsync()
    {
        now += TimeSpan.FromMinutes(1);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
    }

    [Fact]
    public async Task Word_save_goes_into_the_original_item()
    {
        var original = api.Add("report.docx", content: "v1");

        await WriteNewAsync("~WRD0000.tmp", "v2");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default);
        await engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default);
        await engine.DeleteAsync(Root + "~WRL0001.tmp", default);
        await SettleAsync();

        var upload = Assert.Single(api.Uploads);
        Assert.Equal(original.Id, upload.ItemId);
        Assert.Equal(original.ETag, upload.IfMatch);
        Assert.Equal("v2", api.ContentOf("report.docx"));
        Assert.Equal(["report.docx"], api.Paths);
        Assert.Equal(0, api.Deletes + api.Moves);
        Assert.Null(await engine.GetEntryAsync(Root + "~WRL0001.tmp", default));
    }

    [Fact]
    public async Task A_mapped_write_without_a_close_uploads_once_writes_go_quiet()
    {
        var original = api.Add("data.bin", content: "v1");
        var entry = (await engine.GetEntryAsync(Root + "data.bin", default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: false, default);
        await File.WriteAllTextAsync(staging, "v2");
        engine.MarkDirty(entry);
        engine.CommitLater(entry);

        now += TimeSpan.FromSeconds(1);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Empty(api.Uploads);

        now += TimeSpan.FromSeconds(5);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal(original.Id, Assert.Single(api.Uploads).ItemId);
        Assert.Equal("v2", api.ContentOf("data.bin"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task Excel_save_goes_into_the_original_item()
    {
        var original = api.Add("book.xlsx", content: "v1");

        await WriteNewAsync("8A3F1C2D", "v2");
        await engine.DeleteAsync(Root + "book.xlsx", default);
        await engine.RenameAsync(Root + "8A3F1C2D", Root + "book.xlsx", false, default);
        await SettleAsync();

        Assert.Equal(original.Id, Assert.Single(api.Uploads).ItemId);
        Assert.Equal("v2", api.ContentOf("book.xlsx"));
        Assert.Equal(["book.xlsx"], api.Paths);
        Assert.Equal(0, api.Deletes);
    }

    [Fact]
    public async Task In_place_save_updates_the_item()
    {
        var original = api.Add("notes.txt", content: "v1");

        await OverwriteAsync("notes.txt", "v2");

        Assert.Equal(original.Id, Assert.Single(api.Uploads).ItemId);
        Assert.Equal("v2", api.ContentOf("notes.txt"));
        var entry = (await engine.GetEntryAsync(Root + "notes.txt", default))!;
        Assert.Null(entry.StagingPath);
        Assert.Equal(2, entry.Size);
    }

    [Fact]
    public async Task New_file_uploads_on_close_and_temp_files_never_do()
    {
        await WriteNewAsync("new.txt", "hello");
        await WriteNewAsync("~$new.docx", "owner");
        await WriteNewAsync("scratch.tmp", "temp");

        Assert.Equal("new.txt", Assert.Single(api.Uploads).Name);
        Assert.Equal(["new.txt"], api.Paths);
        Assert.NotNull(await engine.GetEntryAsync(Root + "~$new.docx", default));
    }

    [Fact]
    public async Task Delete_waits_for_the_settle_window()
    {
        api.Add("old.txt");

        await engine.DeleteAsync(Root + "old.txt", default);
        Assert.Null(await engine.GetEntryAsync(Root + "old.txt", default));
        now += TimeSpan.FromSeconds(5);
        await engine.ProcessDueAsync(false, default);
        Assert.Equal(0, api.Deletes);

        await SettleAsync();
        Assert.Equal(1, api.Deletes);
        Assert.Empty(api.Paths);
    }

    [Fact]
    public async Task Close_with_a_locked_file_saves_a_conflict_copy()
    {
        api.Add("notes.txt", content: "v1");
        api.FailUpload = target => target.ItemId is not null ? new RemoteException(RemoteError.Locked, "423") : null;

        await OverwriteAsync("notes.txt", "mine");

        Assert.Equal("v1", api.ContentOf("notes.txt"));
        var copy = Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
        Assert.Equal("mine", api.ContentOf(copy));
    }

    [Fact]
    public async Task Word_save_into_a_locked_file_fails_and_restores_the_original()
    {
        api.Add("report.docx", content: "v1");
        api.FailUpload = target => target.ItemId is not null ? new RemoteException(RemoteError.Locked, "423") : null;

        await WriteNewAsync("~WRD0000.tmp", "v2");
        await engine.RenameAsync(Root + "report.docx", Root + "~WRL0001.tmp", false, default);
        var failure = await Assert.ThrowsAsync<RemoteException>(() => engine.RenameAsync(Root + "~WRD0000.tmp", Root + "report.docx", false, default));
        await SettleAsync();

        Assert.Equal(RemoteError.Locked, failure.Error);
        Assert.Equal("v1", api.ContentOf("report.docx"));
        Assert.NotNull(await engine.GetEntryAsync(Root + "report.docx", default));
        Assert.Null(await engine.GetEntryAsync(Root + "~WRL0001.tmp", default));
        Assert.NotNull(await engine.GetEntryAsync(Root + "~WRD0000.tmp", default));
    }

    [Fact]
    public async Task Unsaved_changes_survive_a_crash()
    {
        var original = api.Add("notes.txt", content: "v1");
        var entry = (await engine.GetEntryAsync(Root + "notes.txt", default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: false, default);
        await File.WriteAllTextAsync(staging, "v2 unsaved");
        engine.MarkDirty(entry);
        var temp = await engine.CreateAsync(Root + "~WRD0001.tmp", false, default);
        await File.WriteAllTextAsync(temp.StagingPath!, "temp");

        // The agent dies before the close; a new one starts on the same staging folder
        var restarted = new DriveEngine(
            DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("b!x")),
            api, new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), Clock = () => now });
        var recovered = restarted.Recover();
        await restarted.ProcessDueAsync(false, default);
        await restarted.WaitForUploadsAsync();

        Assert.Equal(1, recovered);
        Assert.Equal(original.Id, Assert.Single(api.Uploads).ItemId);
        Assert.Equal("v2 unsaved", api.ContentOf("notes.txt"));
        Assert.Empty(Directory.GetFiles(Path.Combine(directory, "staging")));
    }

    [Fact]
    public async Task Rename_and_folders_go_straight_to_the_server()
    {
        api.Add("a.txt");

        await engine.RenameAsync(Root + "a.txt", Root + "b.txt", false, default);
        await engine.CreateAsync(Root + "Folder", true, default);
        await engine.RenameAsync(Root + "b.txt", Root + "Folder\\b.txt", false, default);

        Assert.Equal(2, api.Moves);
        Assert.Equal(["Folder", "Folder/b.txt"], api.Paths);
        Assert.Null(await engine.GetEntryAsync(Root + "a.txt", default));
        Assert.NotNull(await engine.GetEntryAsync(Root + "Folder\\b.txt", default));
    }

    [Fact]
    public async Task Rules_of_the_fixed_tree()
    {
        api.Add("a.txt");

        var move = await Assert.ThrowsAsync<FsException>(() => engine.RenameAsync(Root + "a.txt", "\\Sites\\Finance\\Documents\\a.txt", false, default));
        var create = await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync("\\Sites\\new.txt", false, default));
        var exists = await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync(Root + "a.txt", false, default));

        Assert.Equal(FsError.NotSameDevice, move.Error);
        Assert.Equal(FsError.AccessDenied, create.Error);
        Assert.Equal(FsError.NameCollision, exists.Error);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}
