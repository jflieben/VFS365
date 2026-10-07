using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

public sealed class DatabaseFilesTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));

    DriveEngine Create(FakeDriveApi api, DatabaseFilePolicy policy) =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"), DriveScope.OneDrive),
            api, new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions { StagingDirectory = Path.Combine(directory, "staging"), DatabaseFiles = policy, WalkThreshold = 0 });

    [Fact]
    public async Task Allowed_database_files_are_ordinary_files()
    {
        var api = new FakeDriveApi();
        api.Add("orders.accdb");
        var engine = Create(api, DatabaseFilePolicy.Allow);

        var entry = (await engine.GetEntryAsync("\\orders.accdb", default))!;

        Assert.False(entry.ReadOnly);
        Assert.NotNull(await engine.OpenForWriteAsync(entry, truncate: false, default));
    }

    [Fact]
    public async Task Read_only_database_files_open_for_reading_but_never_change()
    {
        var api = new FakeDriveApi();
        api.Add("orders.accdb");
        api.Add("notes.txt");
        var engine = Create(api, DatabaseFilePolicy.ReadOnly);

        var database = (await engine.GetEntryAsync("\\orders.accdb", default))!;
        var text = (await engine.GetEntryAsync("\\notes.txt", default))!;

        Assert.True(database.ReadOnly);
        Assert.False(database.Blocked);
        Assert.False(text.ReadOnly);
        await Assert.ThrowsAsync<FsException>(() => engine.OpenForWriteAsync(database, truncate: false, default));
        await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync("\\orders.laccdb", false, default));
        await Assert.ThrowsAsync<FsException>(() => engine.CreateAsync("\\new.sqlite", false, default));
        using var content = await engine.OpenContentAsync(database, default);
        Assert.Equal(3, content.Length);
    }

    [Fact]
    public async Task Blocked_database_files_are_listed_but_marked()
    {
        var api = new FakeDriveApi();
        api.Add("company.QBW");
        var engine = Create(api, DatabaseFilePolicy.Block);

        var listed = await engine.ListAsync((await engine.GetEntryAsync("\\", default))!, default);

        Assert.True(listed.Single().Blocked);
        Assert.True(listed.Single().ReadOnly);
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
