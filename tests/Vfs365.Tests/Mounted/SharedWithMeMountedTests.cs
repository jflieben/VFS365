using System.Text;
using Vfs365.Core.Discovery;
using Vfs365.Core.Drive;

namespace Vfs365.Tests.Mounted;

/// <summary>Shared with me through WinFsp: shared files read and refuse changes with an error, shared folders work like library folders.</summary>
[Collection(MountedCollection.Name)]
public sealed class SharedWithMeMountedTests
{
    static DriveNamespace Namespace() => DriveNamespace.Build(new MyDrive("b!me", "https://x-my.sharepoint.com/personal/u"), [], (_, _) => Task.FromResult("unused"),
        DriveScope.All, shared:
        [
            new SharedItem("b!alice", "id-Docs/Project", "Project", true, "Docs/Project", "Alice"),
            new SharedItem("b!alice", "id-Docs/Report.docx", "Report.docx", false, "Docs/Report.docx", "Alice", Size: 5),
        ]);

    static MountedDrive Start() => MountedDrive.Start(ns: Namespace(), seed: fake =>
    {
        fake.Add("Docs", folder: true);
        fake.Add("Docs/Project", folder: true);
        fake.Add("Docs/Project/plan.docx", content: "v1");
        fake.Add("Docs/Report.docx", content: "alice");
        fake.Add("Docs/private.txt", content: "not shared");
    });

    static Exception? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
    }

    [MountedFact]
    public async Task A_shared_file_reads_and_every_change_at_the_top_fails_with_an_error()
    {
        using var drive = Start();
        var shared = drive.PathOf("Shared with me");
        var report = Path.Combine(shared, "Report.docx");

        Assert.Equal(["Project", "Report.docx"], Directory.GetFileSystemEntries(shared).Select(Path.GetFileName).Order());
        Assert.Equal("alice", File.ReadAllText(report));
        Assert.True(new FileInfo(report).IsReadOnly);

        Exception?[] refused =
        [
            Try(() => File.WriteAllText(report, "changed")),
            Try(() => File.Delete(report)),
            Try(() => File.Move(report, Path.Combine(shared, "Renamed.docx"))),
            Try(() => File.WriteAllText(Path.Combine(shared, "~WRD0001.tmp"), "temp")),
            Try(() => Directory.CreateDirectory(Path.Combine(shared, "New folder"))),
            Try(() => Directory.Delete(Path.Combine(shared, "Project"))),
            Try(() => Directory.Move(Path.Combine(shared, "Project"), Path.Combine(shared, "Other"))),
            Try(() => SavePatterns.Word(report, Encoding.UTF8.GetBytes("word"), 1)),
        ];
        await drive.SettleAsync();

        Assert.All(refused, e => Assert.True(e is IOException or UnauthorizedAccessException, e?.ToString() ?? "not refused"));
        Assert.Equal(["Docs", "Docs/Project", "Docs/Project/plan.docx", "Docs/Report.docx", "Docs/private.txt"], drive.ServerTree());
        Assert.Equal("alice", drive.Fake.ContentOf("Docs/Report.docx"));
        Assert.Empty(drive.Fake.Uploads);
        Assert.Equal(0, drive.Fake.Deletes + drive.Fake.Moves);
    }

    [MountedFact]
    public async Task A_shared_folder_takes_new_files_and_Word_saves_in_the_owners_drive()
    {
        using var drive = Start();
        var project = drive.PathOf(@"Shared with me\Project");
        var original = drive.Fake.ItemAt("Docs/Project/plan.docx")!;

        File.WriteAllText(Path.Combine(project, "notes.txt"), "new");
        SavePatterns.Word(Path.Combine(project, "plan.docx"), Encoding.UTF8.GetBytes("v2"), 1);
        await drive.SettleAsync();

        Assert.Equal(["Docs", "Docs/Project", "Docs/Project/notes.txt", "Docs/Project/plan.docx", "Docs/Report.docx", "Docs/private.txt"], drive.ServerTree());
        Assert.Equal("v2", drive.Fake.ContentOf("Docs/Project/plan.docx"));
        Assert.Equal(original.Id, drive.Fake.ItemAt("Docs/Project/plan.docx")!.Id);
        Assert.Equal(["notes.txt", "plan.docx"], Directory.GetFileSystemEntries(project).Select(Path.GetFileName).Order());
        Assert.Empty(drive.Errors());
    }

    [MountedFact]
    public async Task Deleting_a_shared_folder_with_its_content_keeps_the_files_in_it()
    {
        using var drive = Start();

        // Windows deletes the content first; the folder's own delete is refused, and the files whose deletes still wait come back
        var refused = Try(() => Directory.Delete(drive.PathOf(@"Shared with me\Project"), recursive: true));
        await drive.SettleAsync();

        Assert.True(refused is IOException or UnauthorizedAccessException, refused?.ToString() ?? "not refused");
        Assert.Equal("v1", drive.Fake.ContentOf("Docs/Project/plan.docx"));
        Assert.Equal(0, drive.Fake.Deletes);
        Assert.Contains("Shared with me/Project/plan.docx", drive.VolumeTree());
    }
}
