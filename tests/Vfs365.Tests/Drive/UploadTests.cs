using System.Diagnostics;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Uploads outside the write gate: background uploads of new files, parallel uploads, saves repeated quickly, reading ahead.</summary>
public sealed class UploadTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly List<string> log = [];
    DateTimeOffset now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";

    DriveEngine Create(bool background = false, TimeSpan repeatWindow = default, int readAhead = 0) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                UploadNewFilesInBackground = background,
                RepeatSaveWindow = repeatWindow,
                ReadAheadFiles = readAhead,
                Log = line => { lock (log) { log.Add(line); } },
            });

    static async Task WriteNewAsync(DriveEngine engine, string name, string content)
    {
        var entry = await engine.CreateAsync(Root + name, false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + name, default);
    }

    static async Task SaveAsync(DriveEngine engine, string name, string content)
    {
        var entry = (await engine.GetEntryAsync(Root + name, default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: true, default);
        await File.WriteAllTextAsync(staging, content);
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + name, default);
    }

    [Fact]
    public async Task New_files_upload_after_their_close_next_to_each_other()
    {
        var engine = Create(background: true);
        var network = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => network.Task;

        // Every close returns while no upload can finish
        for (var i = 0; i < 8; i++)
        {
            await WriteNewAsync(engine, $"file{i}.txt", $"content {i}");
        }
        Assert.Empty(api.Uploads);
        Assert.Equal(8, engine.Unsaved().Count);
        Assert.Equal(["file0.txt", "file1.txt", "file2.txt", "file3.txt", "file4.txt", "file5.txt", "file6.txt", "file7.txt"],
            (await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default)).Select(e => e.Name).Order());

        await Task.Delay(100);
        network.SetResult();
        await engine.WaitForUploadsAsync();
        Assert.Equal(8, api.Uploads.Count);
        Assert.InRange(api.MaxConcurrentUploads, 2, 4);
        Assert.Equal("content 5", api.ContentOf("file5.txt"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_save_into_an_existing_file_has_uploaded_when_the_close_returns()
    {
        api.Add("report.txt", content: "v1");
        var engine = Create(background: true);

        await SaveAsync(engine, "report.txt", "v2");

        Assert.Single(api.Uploads);
        Assert.Equal("v2", api.ContentOf("report.txt"));
    }

    [Fact]
    public async Task Uploads_of_different_files_run_at_the_same_time()
    {
        api.Add("a.txt", content: "a1");
        api.Add("b.txt", content: "b1");
        var engine = Create();
        var network = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => network.Task;

        // Both closes reach the network while neither upload can finish
        var saves = Task.WhenAll(SaveAsync(engine, "a.txt", "a2"), SaveAsync(engine, "b.txt", "b2"));
        for (var i = 0; i < 100 && api.MaxConcurrentUploads < 2; i++)
        {
            await Task.Delay(20);
        }
        Assert.Equal(2, api.MaxConcurrentUploads);

        network.SetResult();
        await saves;
        Assert.Equal("a2", api.ContentOf("a.txt"));
        Assert.Equal("b2", api.ContentOf("b.txt"));
    }

    [Fact]
    public async Task A_rename_waits_for_the_upload_of_its_file()
    {
        var engine = Create(background: true);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => { started.TrySetResult(); return hold.Task; };

        await WriteNewAsync(engine, "a.txt", "x");
        await started.Task;
        var rename = engine.RenameAsync(Root + "a.txt", Root + "b.txt", false, default);
        await Task.Delay(100);
        Assert.False(rename.IsCompleted);

        hold.SetResult();
        await rename;
        await engine.WaitForUploadsAsync();
        Assert.Equal(["b.txt"], api.Paths);
        Assert.Equal("x", api.ContentOf("b.txt"));
        Assert.Single(api.Uploads);
    }

    [Fact]
    public async Task Saves_repeated_within_the_window_upload_once_at_its_end()
    {
        api.Add("log.txt", content: "0");
        var engine = Create(repeatWindow: TimeSpan.FromSeconds(30));

        for (var i = 1; i <= 5; i++)
        {
            await SaveAsync(engine, "log.txt", $"{i}");
            now += TimeSpan.FromSeconds(2);
        }
        Assert.Single(api.Uploads);
        Assert.Equal("1", api.ContentOf("log.txt"));

        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Single(api.Uploads);

        now += TimeSpan.FromSeconds(25);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal(2, api.Uploads.Count);
        Assert.Equal("5", api.ContentOf("log.txt"));
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task A_file_written_to_while_it_uploads_uploads_again()
    {
        api.Add("data.txt", content: "v1");
        var engine = Create();
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        api.BeforeUpload = () => { started.TrySetResult(); return hold.Task; };

        var entry = (await engine.GetEntryAsync(Root + "data.txt", default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: true, default);
        await File.WriteAllTextAsync(staging, "v2");
        engine.MarkDirty(entry);
        var close = engine.CommitAsync(Root + "data.txt", default);
        await started.Task;

        // Another handle writes while the first close uploads
        await File.WriteAllTextAsync(staging, "v3");
        engine.MarkDirty(entry);
        api.BeforeUpload = null;
        hold.SetResult();
        await close;
        Assert.Single(engine.Unsaved());

        now += TimeSpan.FromSeconds(5);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal("v3", api.ContentOf("data.txt"));
        Assert.Equal(2, api.Uploads.Count);
        Assert.Empty(engine.Unsaved());
    }

    [Fact]
    public async Task Files_created_in_a_folder_the_drive_made_need_no_lookups()
    {
        var engine = Create(background: true);
        await engine.CreateAsync(Root + "new", true, default);
        var lookups = api.GetCalls;

        for (var i = 0; i < 5; i++)
        {
            await WriteNewAsync(engine, $"new\\f{i}.txt", "x");
        }
        await engine.WaitForUploadsAsync();

        Assert.Equal(lookups, api.GetCalls);
        Assert.Equal(5, api.Uploads.Count);
    }

    [Fact]
    public async Task A_copy_out_of_the_drive_gets_the_next_small_files_downloaded_ahead()
    {
        api.Add("photos", folder: true);
        for (var i = 0; i < 20; i++)
        {
            api.Add($"photos/p{i:D2}.jpg", content: $"photo {i}");
        }
        var engine = Create(readAhead: 5);
        var folder = (await engine.GetEntryAsync(Root + "photos", default))!;
        var files = (await engine.ListAsync(folder, default)).OrderBy(f => f.Name).ToList();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal($"photo {i}", await ReadAllAsync(engine, files[i]));
        }
        await Settle(3 + 5);
        Assert.Equal(3 + 5, api.Downloads);

        for (var i = 3; i < 8; i++)
        {
            Assert.Equal($"photo {i}", await ReadAllAsync(engine, files[i]));
        }
        await Settle(8 + 5);
        Assert.Equal(8 + 5, api.Downloads);

        now += TimeSpan.FromSeconds(30);
        await engine.PollActiveAsync();
        Assert.Contains(log, line => line.Contains("10 file(s) downloaded ahead, 5 of them opened"));
    }

    [Fact]
    public async Task Reading_a_couple_of_files_downloads_nothing_ahead()
    {
        api.Add("docs", folder: true);
        for (var i = 0; i < 10; i++)
        {
            api.Add($"docs/d{i}.txt", content: $"doc {i}");
        }
        var engine = Create(readAhead: 5);
        var files = (await engine.ListAsync((await engine.GetEntryAsync(Root + "docs", default))!, default)).OrderBy(f => f.Name).ToList();

        await ReadAllAsync(engine, files[0]);
        await ReadAllAsync(engine, files[4]);
        await Settle();

        Assert.Equal(2, api.Downloads);
    }

    /// <summary>
    /// Waits until at least <paramref name="expected"/> downloads started (background work starts late on a busy build machine), then until
    /// no more start for 50 ms, so one too many still shows.
    /// </summary>
    async Task Settle(int expected = 0)
    {
        for (var waited = 0; api.Downloads < expected && waited < 10_000; waited += 20)
        {
            await Task.Delay(20);
        }
        for (int last = -1, i = 0; i < 100 && last != api.Downloads; i++)
        {
            last = api.Downloads;
            await Task.Delay(50);
        }
    }

    static async Task<string> ReadAllAsync(DriveEngine engine, FsEntry file)
    {
        using var source = await engine.OpenContentAsync(file, default);
        var buffer = new byte[source.Length];
        var read = await source.ReadAsync(0, buffer, default);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
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
