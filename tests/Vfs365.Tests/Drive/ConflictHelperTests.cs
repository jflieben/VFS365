using System.Text;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Drive;

/// <summary>Conflict copies: who saved the other version, and Keep this version.</summary>
public sealed class ConflictHelperTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly List<EngineNotice> notices = [];
    DateTimeOffset now = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
    static readonly DateTimeOffset SavedAt = new(2026, 10, 25, 11, 58, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";

    DriveEngine Create() =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                Notice = notice => { lock (notices) { notices.Add(notice); } },
            });

    static async Task SaveAsync(DriveEngine engine, string name, string content)
    {
        var entry = await engine.StageAsync((await engine.GetEntryAsync(Root + name, default))!, truncate: true, default);
        await File.WriteAllTextAsync(entry.StagingPath!, content);
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + name, default);
    }

    /// <summary>The engine has offer.docx listed, Ann saves it in Microsoft 365, then this PC saves it: a conflict copy. Returns its name.</summary>
    async Task<string> ConflictAsync(DriveEngine engine)
    {
        Assert.NotNull(await engine.GetEntryAsync(Root + "offer.docx", default));
        api.RemoteEdit("offer.docx", "theirs", "Ann de Vries", SavedAt);
        await SaveAsync(engine, "offer.docx", "mine");
        return Assert.Single(api.Paths, p => p.Contains("(conflict", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_conflict_copy_notice_names_who_saved_the_other_version_and_when()
    {
        api.Add("offer.docx", content: "v1");
        var engine = Create();

        var copy = await ConflictAsync(engine);

        var notice = Assert.Single(notices, n => n.Kind == NoticeKind.ConflictCopy);
        Assert.Equal(copy, notice.Detail);
        Assert.Equal("Ann de Vries", notice.By);
        Assert.Equal(SavedAt, notice.At);
        Assert.Equal("theirs", api.ContentOf("offer.docx"));
        Assert.Equal("mine", api.ContentOf(copy));
    }

    [Fact]
    public async Task A_conflict_copy_of_a_locked_file_names_nobody()
    {
        var original = api.Add("offer.docx", content: "v1");
        var engine = Create();
        api.FailUpload = target => target.ItemId == original.Id ? new RemoteException(RemoteError.Locked, "423 locked") : null;

        await SaveAsync(engine, "offer.docx", "mine");

        var notice = Assert.Single(notices, n => n.Kind == NoticeKind.ConflictCopy);
        Assert.Null(notice.By);
    }

    [Fact]
    public async Task Keep_this_version_makes_the_copy_a_new_version_of_the_original_and_deletes_the_copy()
    {
        var original = api.Add("offer.docx", content: "v1");
        var engine = Create();
        var copy = await ConflictAsync(engine);
        var theirs = api.ItemAt("offer.docx")!;

        var kept = await engine.KeepVersionAsync(Root + copy, default);

        Assert.Equal(Root + "offer.docx", kept);
        Assert.Equal("mine", api.ContentOf("offer.docx"));
        Assert.Equal(original.Id, api.ItemAt("offer.docx")!.Id); // the same item: Ann's save stays in its version history
        var upload = api.Uploads[^1];
        Assert.Equal((original.Id, theirs.ETag), (upload.ItemId, upload.IfMatch));

        now += TimeSpan.FromSeconds(11); // the copy goes after the settle window, like any delete
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.DoesNotContain(copy, api.Paths);
        Assert.Equal("mine", Encoding.UTF8.GetString(api.BytesOf("offer.docx")!));
    }

    [Fact]
    public async Task Keep_this_version_refuses_when_the_original_changed_again_since_the_copy_was_made()
    {
        api.Add("offer.docx", content: "v1");
        var engine = Create();
        var copy = await ConflictAsync(engine);
        Assert.NotNull(await engine.GetEntryAsync(Root + "offer.docx", default)); // listed with Ann's version
        api.RemoteEdit("offer.docx", "theirs again", "Ann de Vries", SavedAt.AddMinutes(1));
        now += TimeSpan.FromMinutes(5);
        await engine.PollAsync("b!me"); // the change feed brings it
        var attempts = 0;
        api.FailUpload = target => { attempts++; return null; };

        var refused = await Assert.ThrowsAsync<RemoteException>(() => engine.KeepVersionAsync(Root + copy, default));

        Assert.Equal(RemoteError.Conflict, refused.Error);
        Assert.Equal(0, attempts); // refused before any upload
        Assert.Equal("theirs again", api.ContentOf("offer.docx"));
        Assert.Equal("mine", api.ContentOf(copy));
    }

    [Fact]
    public async Task Keep_this_version_refuses_when_the_original_changed_after_it_was_listed()
    {
        // A copy from an earlier run (nothing remembered about it): the upload's If-Match catches the newer version
        api.Add("offer.docx", content: "theirs");
        var copy = api.Add("offer (conflict 2026-10-24 101112).docx", content: "mine").Name;
        var engine = Create();
        Assert.NotNull(await engine.GetEntryAsync(Root + "offer.docx", default));
        api.RemoteEdit("offer.docx", "theirs again");

        var refused = await Assert.ThrowsAsync<RemoteException>(() => engine.KeepVersionAsync(Root + copy, default));

        Assert.Equal(RemoteError.Conflict, refused.Error);
        Assert.Equal("theirs again", api.ContentOf("offer.docx"));
        Assert.Equal("mine", api.ContentOf(copy));
    }

    [Fact]
    public async Task Keep_this_version_only_takes_conflict_copies()
    {
        api.Add("offer.docx", content: "v1");
        api.Add("offer (copy).docx", content: "v2");
        var engine = Create();

        var refused = await Assert.ThrowsAsync<FsException>(() => engine.KeepVersionAsync(Root + "offer (copy).docx", default));

        Assert.Equal(FsError.Invalid, refused.Error);
        Assert.Equal("v1", api.ContentOf("offer.docx"));
    }

    [Theory]
    [InlineData("Offer (conflict 2026-10-25 143012).docx", "Offer.docx")]
    [InlineData("Offer (conflict 2026-10-25 143012 2).docx", "Offer.docx")]
    [InlineData("data.tar (recovered 2026-10-25 143012).gz", "data.tar.gz")]
    [InlineData("Makefile (conflict 2026-10-25 143012)", "Makefile")]
    [InlineData(" (conflict 2026-10-25 143012).gitignore", ".gitignore")]
    [InlineData("Offer (copy).docx", null)]
    [InlineData("Offer (conflict 2026-10-25).docx", null)]
    [InlineData("Offer.docx", null)]
    public void Copy_names_lead_back_to_the_original(string name, string? original) => Assert.Equal(original, ConflictCopies.OriginalName(name));

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
