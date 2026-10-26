using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Per-file states for Explorer's overlay icons (DriveEngine.FileStates), from listings, local state and the content cache.</summary>
public sealed class FileStatesTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly ContentCache cache;
    readonly DriveEngine engine;
    DateTimeOffset now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";

    public FileStatesTests()
    {
        cache = new ContentCache(Path.Combine(directory, "cache"), api);
        engine = new DriveEngine(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api, cache, new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), Clock = () => now });
    }

    FileState? StateOf(string path) =>
        engine.FileStates().Where(s => s.Path.Equals(path, StringComparison.OrdinalIgnoreCase)).Select(s => (FileState?)s.State).FirstOrDefault();

    async Task<FileState?> EventuallyAsync(string path, FileState? expected)
    {
        for (var i = 0; i < 200 && StateOf(path) != expected; i++)
        {
            await Task.Delay(10);
        }
        return StateOf(path);
    }

    async Task ReadAllAsync(string path)
    {
        using var source = await engine.OpenContentAsync((await engine.GetEntryAsync(path, default))!, default);
        var buffer = new byte[4096];
        for (long offset = 0; offset < source.Length;)
        {
            offset += await source.ReadAsync(offset, buffer, default);
        }
    }

    async Task<FsEntry> WriteNewAsync(string name, string content)
    {
        var entry = await engine.CreateAsync(Root + name, false, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        return entry;
    }

    [Fact]
    public async Task Listed_files_are_online_only_until_their_content_is_here()
    {
        api.Add("a", folder: true);
        var one = api.Add("a/one.txt", content: "one");
        api.Add("a/two.txt", content: "two");
        await engine.ListAsync((await engine.GetEntryAsync(Root + "a", default))!, default);
        Assert.Empty(engine.FileStates());

        await ReadAllAsync(Root + "a\\one.txt");

        Assert.Equal(FileState.Cached, await EventuallyAsync(Root + "a\\one.txt", FileState.Cached));
        Assert.Null(StateOf(Root + "a\\two.txt"));
        Assert.Null(StateOf(Root + "a")); // folders get no state of their own here

        // Evicted: online only again (the listing's cached files are found again when its drive's cache changes)
        cache.ForgetItem("b!me", one.Id);
        Assert.Null(StateOf(Root + "a\\one.txt"));
    }

    [Fact]
    public async Task Content_a_reader_waits_for_is_downloading()
    {
        api.Add("big.bin", content: "content");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        var hold = new TaskCompletionSource();
        api.BeforeDownload = () => hold.Task;

        var read = Task.Run(() => ReadAllAsync(Root + "big.bin"));
        Assert.Equal(FileState.Downloading, await EventuallyAsync(Root + "big.bin", FileState.Downloading));
        hold.SetResult();
        await read;

        Assert.Equal(FileState.Cached, await EventuallyAsync(Root + "big.bin", FileState.Cached));
    }

    [Fact]
    public async Task Changed_files_are_uploading_until_uploaded_then_on_this_device()
    {
        var signals = 0;
        engine.StatesChanged += () => Interlocked.Increment(ref signals);
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default); // Explorer shows the folder
        var before = engine.StatesVersion();

        await WriteNewAsync("new.txt", "hello");
        Assert.Equal(FileState.Uploading, StateOf(Root + "new.txt"));
        Assert.NotEqual(before, engine.StatesVersion());

        await engine.CommitAsync(Root + "new.txt", default);
        await engine.WaitForUploadsAsync();

        Assert.Equal("hello", api.ContentOf("new.txt"));
        Assert.Equal(FileState.Cached, await EventuallyAsync(Root + "new.txt", FileState.Cached));
        Assert.True(Volatile.Read(ref signals) > 0);
    }

    [Fact]
    public async Task Refused_uploads_are_problems_and_a_first_failure_still_uploading()
    {
        api.FailUpload = target => target.Name == "refused.txt"
            ? new RemoteException(RemoteError.Rejected, "400 invalidRequest: the name isn't allowed")
            : new HttpRequestException("No such host is known. (contoso.sharepoint.com:443)");
        await WriteNewAsync("refused.txt", "x");
        await WriteNewAsync("offline.txt", "y");
        Assert.Equal(FileState.Uploading, StateOf(Root + "offline.txt"));

        await engine.CommitAsync(Root + "refused.txt", default);
        await engine.CommitAsync(Root + "offline.txt", default);

        Assert.Equal(FileState.Problem, StateOf(Root + "refused.txt"));
        Assert.Equal(FileState.Uploading, StateOf(Root + "offline.txt")); // Problem only after ProblemAfterFailures in a row
        Assert.Empty(api.Paths);
    }

    [Fact]
    public async Task A_refused_delete_is_a_problem_for_a_while()
    {
        var locked = api.Add("locked.txt", content: "keep");
        api.FailDelete = id => id == locked.Id ? new RemoteException(RemoteError.Locked, "423 locked") : null;
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);

        await engine.DeleteAsync(Root + "locked.txt", default);
        now += TimeSpan.FromMinutes(1);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();

        Assert.Equal(FileState.Problem, StateOf(Root + "locked.txt"));
        var version = engine.StatesVersion();
        now += TimeSpan.FromMinutes(11);
        Assert.NotEqual(version, engine.StatesVersion());
        Assert.Null(StateOf(Root + "locked.txt"));
    }

    [Fact]
    public async Task A_file_deleted_here_has_no_state_and_local_state_wins_over_the_listing()
    {
        api.Add("gone.txt", content: "a");
        api.Add("kept.txt", content: "b");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        await ReadAllAsync(Root + "gone.txt");
        await ReadAllAsync(Root + "kept.txt");
        Assert.Equal(FileState.Cached, await EventuallyAsync(Root + "gone.txt", FileState.Cached));

        await engine.DeleteAsync(Root + "gone.txt", default);
        var kept = await engine.StageAsync((await engine.GetEntryAsync(Root + "kept.txt", default))!, truncate: true, default);
        await File.WriteAllTextAsync(kept.StagingPath!, "changed");
        engine.MarkDirty(kept);

        Assert.Null(StateOf(Root + "gone.txt"));
        Assert.Equal(FileState.Uploading, StateOf(Root + "kept.txt"));
    }

    [Fact]
    public async Task The_version_stays_while_nothing_changes()
    {
        api.Add("a.txt");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        await ReadAllAsync(Root + "a.txt");
        await EventuallyAsync(Root + "a.txt", FileState.Cached);

        var version = engine.StatesVersion();
        var first = engine.FileStates();
        Assert.Equal(version, engine.StatesVersion());
        Assert.Equal(first, engine.FileStates());
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
        catch (UnauthorizedAccessException)
        {
        }
    }
}
