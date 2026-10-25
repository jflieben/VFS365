using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Vfs365.Tests.Mounted;

/// <summary>The volume itself: mounting, the operation guard, and System.IO basics through WinFsp.</summary>
[Collection(MountedCollection.Name)]
public sealed class MountTests(ITestOutputHelper output)
{
    [MountedFact]
    public void The_custom_guard_replaces_WinFsps_and_the_FINE_one_can_be_kept()
    {
        var watch = Stopwatch.StartNew();
        using (var drive = MountedDrive.Start(winFspGuard: false))
        {
            output.WriteLine($"mounted in {watch.ElapsedMilliseconds} ms");
            Assert.False(drive.NamespaceLocked, string.Join("\n", drive.Lines()));
            Assert.Empty(Directory.GetFileSystemEntries(drive.Root));
        }
        using (var drive = MountedDrive.Start(winFspGuard: true))
        {
            Assert.True(drive.NamespaceLocked);
            Assert.Empty(Directory.GetFileSystemEntries(drive.Root));
        }
    }

    [MountedFact]
    public async Task Files_written_read_renamed_and_deleted_reach_the_server()
    {
        using var drive = MountedDrive.Start(seed: fake =>
        {
            fake.Add("docs", folder: true);
            fake.Add("docs/a.txt", content: "hello");
        });
        Assert.Equal("hello", File.ReadAllText(drive.PathOf("docs/a.txt")));
        File.WriteAllText(drive.PathOf("docs/b.txt"), "new file");
        File.Move(drive.PathOf("docs/b.txt"), drive.PathOf("docs/c.txt"));
        File.WriteAllText(drive.PathOf("docs/a.txt"), "changed");
        File.Delete(drive.PathOf("docs/c.txt"));
        Directory.CreateDirectory(drive.PathOf("docs/sub"));
        File.WriteAllBytes(drive.PathOf("docs/sub/empty.bin"), []);
        await drive.SettleAsync();

        Assert.Equal(["docs", "docs/a.txt", "docs/sub", "docs/sub/empty.bin"], drive.ServerTree());
        Assert.Equal(drive.ServerTree(), drive.VolumeTree());
        Assert.Equal("changed", drive.Fake.ContentOf("docs/a.txt"));
        Assert.Equal("changed", File.ReadAllText(drive.PathOf("docs/a.txt")));
        Assert.Empty(File.ReadAllBytes(drive.PathOf("docs/sub/empty.bin")));
        Assert.Empty(drive.Errors());
    }

    [MountedFact]
    public void An_exact_name_search_is_a_lookup_and_finds_names_in_any_case()
    {
        using var drive = MountedDrive.Start(trace: true, seed: fake =>
        {
            fake.Add("docs", folder: true);
            fake.Add("docs/Report.docx", content: "x");
        });
        Assert.Equal(["Report.docx"], Directory.GetFiles(drive.PathOf("docs"), "report.DOCX").Select(Path.GetFileName));
        Assert.Empty(Directory.GetFiles(drive.PathOf("docs"), "missing.txt"));
        // WinFsp passes the name upper-cased (a case-insensitive volume)
        Assert.Contains(drive.Lines(), line => line.Equals(@"dirinfo \docs report.docx: found", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(drive.Lines(), line => line.Equals(@"dirinfo \docs missing.txt: not found", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(drive.Lines(), line => line.StartsWith(@"readdir \docs", StringComparison.Ordinal));

        Assert.Equal(["Report.docx"], Directory.GetFiles(drive.PathOf("docs"), "*.docx").Select(Path.GetFileName));
        Assert.Contains(drive.Lines(), line => line.StartsWith(@"readdir \docs", StringComparison.Ordinal));
    }

    [MountedFact]
    public void Text_round_trips_through_the_volume()
    {
        using var drive = MountedDrive.Start();
        var text = string.Concat(Enumerable.Repeat("VFS365 ", 100_000));
        File.WriteAllText(drive.PathOf("big.txt"), text, Encoding.UTF8);
        Assert.Equal(text, File.ReadAllText(drive.PathOf("big.txt"), Encoding.UTF8));
    }
}
