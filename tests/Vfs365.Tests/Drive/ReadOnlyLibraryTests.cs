using System.Net;
using Vfs365.Core;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;
using Vfs365.Graph;

namespace Vfs365.Tests.Drive;

/// <summary>SharePoint keeps a site read-only for a while (403 serviceReadOnly): the change feed backs off, saves wait and retry.</summary>
public sealed class ReadOnlyLibraryTests : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "vfs365-tests", Guid.NewGuid().ToString("N"));
    readonly FakeDriveApi api = new();
    readonly List<string> log = [];
    readonly List<EngineNotice> notices = [];
    DateTimeOffset now = new(2026, 10, 7, 13, 0, 0, TimeSpan.Zero);

    const string Root = "\\OneDrive\\";

    DriveEngine Create() =>
        new(DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused")),
            api,
            new ContentCache(Path.Combine(directory, "cache"), api),
            new DriveEngineOptions
            {
                StagingDirectory = Path.Combine(directory, "staging"),
                Clock = () => now,
                Log = line => { lock (log) { log.Add(line); } },
                Notice = notice => { lock (notices) { notices.Add(notice); } },
            });

    int Logged(string text)
    {
        lock (log)
        {
            return log.Count(line => line.Contains(text));
        }
    }

    [Fact]
    public async Task The_change_feed_of_a_read_only_library_backs_off_and_logs_once()
    {
        var engine = Create();
        await engine.PollAsync("b!me");
        await engine.ListAsync((await engine.GetEntryAsync("\\OneDrive", default))!, default);
        api.DeltaReadOnly = true;

        await engine.PollAsync("b!me");
        var calls = api.DeltaCalls;
        Assert.Equal(1, Logged("read-only for now"));

        // Hot checks every 20 s and pushes don't read again during the back-off
        for (var i = 0; i < 2; i++)
        {
            now += TimeSpan.FromSeconds(25);
            await engine.PollActiveAsync();
            await engine.PollAsync("b!me");
        }
        Assert.Equal(calls, api.DeltaCalls);

        now += TimeSpan.FromSeconds(15);
        await engine.PollActiveAsync();
        Assert.Equal(calls + 1, api.DeltaCalls);
        Assert.Equal(1, Logged("read-only for now"));

        // The next back-off is twice as long
        now += TimeSpan.FromSeconds(90);
        await engine.PollAsync("b!me");
        Assert.Equal(calls + 1, api.DeltaCalls);

        api.DeltaReadOnly = false;
        now += TimeSpan.FromSeconds(40);
        await engine.PollAsync("b!me");
        Assert.Equal(calls + 2, api.DeltaCalls);
        Assert.Equal(1, Logged("works again"));
        Assert.Contains(log, line => line.Contains("\\OneDrive (drive b!me)"));
    }

    [Fact]
    public async Task A_save_into_a_read_only_library_waits_and_retries_less_often()
    {
        api.Add("report.txt", content: "v1");
        var engine = Create();
        api.FailUpload = _ => new RemoteException(RemoteError.ReadOnly, "403 serviceReadOnly");

        var entry = (await engine.GetEntryAsync(Root + "report.txt", default))!;
        var staging = await engine.OpenForWriteAsync(entry, truncate: true, default);
        await File.WriteAllTextAsync(staging, "v2");
        engine.MarkDirty(entry);
        await engine.CommitAsync(Root + "report.txt", default);

        Assert.Single(engine.Unsaved());
        Assert.Contains(notices, n => n.Kind == NoticeKind.UploadWaiting && n.Detail!.Contains("read-only for now"));

        // First retry after 30 s, the next one only after another 60 s
        now += TimeSpan.FromSeconds(31);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal(2, Logged("upload of report.txt failed"));
        now += TimeSpan.FromSeconds(31);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal(2, Logged("upload of report.txt failed"));

        api.FailUpload = null;
        now += TimeSpan.FromSeconds(30);
        await engine.ProcessDueAsync(false, default);
        await engine.WaitForUploadsAsync();
        Assert.Equal("v2", api.ContentOf("report.txt"));
        Assert.Empty(engine.Unsaved());
        Assert.Contains(notices, n => n.Kind == NoticeKind.UploadDone);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied","innerError":{"code":"serviceReadOnly"},"message":"Database Is Read Only"}}""", RemoteError.ReadOnly)]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":"accessDenied","message":"Access denied"}}""", RemoteError.AccessDenied)]
    public void SharePoint_read_only_answers_are_recognised(HttpStatusCode status, string body, RemoteError expected) =>
        Assert.Equal(expected, new M365RequestException(status, new Uri("https://graph.microsoft.com/v1.0/drives/x/root/delta"), body).Error);

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
