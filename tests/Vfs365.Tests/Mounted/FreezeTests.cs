using System.Diagnostics;
using System.Text;
using Xunit.Abstractions;

namespace Vfs365.Tests.Mounted;

/// <summary>
/// The drive-wide freeze (review P1 1): something slow runs (an upload inside a rename, a folder delete, a lookup inside a create)
/// while other threads open, read and list other files every 50 ms. Measured with WinFsp's FINE guard and with the custom one.
/// WinFsp's driver holds its rename lock exclusively for a whole rename and shared for every create, so opens wait for a slow rename
/// with either guard; the FINE guard also holds every listing, create and delete behind slow creates, deletes and renames.
/// </summary>
[Collection(MountedCollection.Name)]
public sealed class FreezeTests(ITestOutputHelper output)
{
    static readonly TimeSpan Slow = TimeSpan.FromSeconds(2);
    static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    const int Others = 20;

    static byte[] Data(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    static void Seed(Tests.Drive.FakeDriveApi fake)
    {
        fake.Add("docs", folder: true);
        fake.Add("docs/report.docx", Data(100_000, 1));
        fake.Add("docs/book.xlsx", Data(100_000, 2));
        fake.Add("other", folder: true);
        for (var i = 1; i <= Others; i++)
        {
            fake.Add($"other/f{i:00}.txt", content: $"other file {i}");
        }
        fake.Add("old", folder: true);
        fake.Add("far", folder: true);
    }

    /// <summary>The slow part of each scenario, set up on the drive, and what runs it.</summary>
    static Action Scenario(string name, MountedDrive drive)
    {
        switch (name)
        {
            case "word":
                drive.Api.UploadDelay = file => file == "report.docx" ? Slow : TimeSpan.Zero;
                return () => SavePatterns.Word(drive.PathOf("docs/report.docx"), Data(120_000, 3), 1);
            case "excel":
                drive.Api.UploadDelay = file => file == "book.xlsx" ? Slow : TimeSpan.Zero;
                return () => SavePatterns.Excel(drive.PathOf("docs/book.xlsx"), Data(120_000, 4), new Random(5));
            case "download":
                drive.Api.UploadDelay = file => file.StartsWith("setup.zip", StringComparison.Ordinal) ? Slow : TimeSpan.Zero;
                return () => SavePatterns.ChromeDownload(drive.PathOf("docs/setup.zip"), Data(300_000, 6));
            case "folder-rename":
                drive.Api.UploadDelay = file => file.StartsWith("queued", StringComparison.Ordinal) ? Slow : TimeSpan.Zero;
                Directory.CreateDirectory(drive.PathOf("batch"));
                for (var i = 0; i < 8; i++)
                {
                    File.WriteAllText(drive.PathOf($"batch/queued{i}.txt"), $"queued {i}");
                }
                return () => Directory.Move(drive.PathOf("batch"), drive.PathOf("batch2"));
            case "folder-delete":
                drive.Api.DeleteDelay = item => item == "old" ? Slow : TimeSpan.Zero;
                return () => Directory.Delete(drive.PathOf("old"));
            case "lookup":
                drive.Api.LookupDelay = path => path == "far/new.txt" ? Slow : TimeSpan.Zero;
                return () => File.WriteAllText(drive.PathOf("far/new.txt"), "created where the lookup is slow");
            default:
                throw new ArgumentException(name);
        }
    }

    public static TheoryData<string, bool> Cases()
    {
        var cases = new TheoryData<string, bool>();
        foreach (var scenario in new[] { "word", "excel", "download", "folder-rename", "folder-delete", "lookup" })
        {
            cases.Add(scenario, true);
            cases.Add(scenario, false);
        }
        return cases;
    }

    [MountedTheory]
    [MemberData(nameof(Cases))]
    public async Task Other_files_during_a_slow_operation(string scenario, bool winFspGuard)
    {
        using var drive = MountedDrive.Start(winFspGuard, Seed);
        Assert.Equal(winFspGuard, drive.NamespaceLocked);
        // Listed and cached, as files a user just worked with
        Directory.GetFiles(drive.Root);
        Directory.GetFiles(drive.PathOf("docs"));
        for (var i = 1; i <= Others; i++)
        {
            File.ReadAllBytes(drive.PathOf($"other/f{i:00}.txt"));
        }
        using var folder = Native.OpenFolder(drive.PathOf("other"));
        using var open = File.OpenHandle(drive.PathOf("other/f01.txt"));
        var slow = Scenario(scenario, drive);

        var next = 0;
        var buffer = new byte[64];
        long started, ended;
        using (var probes = new Probes(Interval,
            ("open+read", () => File.ReadAllBytes(drive.PathOf($"other/f{Interlocked.Increment(ref next) % Others + 1:00}.txt"))),
            ("list", () => Directory.GetFiles(drive.PathOf("other"))),
            ("list open handle", () => Native.ListFromStart(folder)),
            ("read open handle", () => RandomAccess.Read(open, buffer, 0)),
            ("create+delete", () =>
            {
                var probe = drive.PathOf($"other/~$probe{Environment.CurrentManagedThreadId}.txt");
                File.WriteAllText(probe, "x");
                File.Delete(probe);
            })))
        {
            Thread.Sleep(300);
            started = Stopwatch.GetTimestamp();
            slow();
            ended = Stopwatch.GetTimestamp();
            Thread.Sleep(200);
            Assert.Empty(probes.Errors);

            var took = Stopwatch.GetElapsedTime(started, ended);
            var report = new StringBuilder($"{scenario}, {(winFspGuard ? "FINE guard" : "custom guard")}: slow part {took.TotalMilliseconds:0} ms;");
            var worst = new Dictionary<string, double>();
            foreach (var kind in new[] { "open+read", "list", "list open handle", "read open handle", "create+delete" })
            {
                var (ms, count) = probes.Worst(kind, started, ended);
                worst[kind] = ms;
                report.Append($" {kind} worst {ms:0} ms ({count});");
            }
            output.WriteLine(report.ToString());

            if (scenario is "download" or "folder-rename")
            {
                // The upload goes on after the rename (a new file's item follows its name; uploads below a folder use its ID): the
                // driver's rename lock is held only briefly, so nothing waits whichever the guard
                Assert.True(took < Slow / 2, $"the rename took {took.TotalMilliseconds:0} ms");
                Assert.True(worst.Values.All(ms => ms < 1000), report.ToString());
            }
            else
            {
                Assert.True(took >= Slow * 0.9, $"the slow part took {took.TotalMilliseconds:0} ms");
            }
            Assert.True(worst["read open handle"] < 1000, report.ToString()); // file I/O is never guarded
            if (scenario is "folder-delete" or "lookup")
            {
                // Outside renames only the guard serializes opens and listings: FINE holds them all, the custom guard none. A folder
                // delete still holds creates elsewhere: the engine sends its DELETE with the write gate held.
                if (winFspGuard)
                {
                    Assert.True(worst["open+read"] >= 1000, report.ToString());
                }
                else
                {
                    Assert.True(worst["open+read"] < 1000 && worst["list"] < 1000, report.ToString());
                    Assert.True(scenario == "folder-delete" || worst["create+delete"] < 1000, report.ToString());
                }
            }
            if (!winFspGuard)
            {
                Assert.True(worst["list open handle"] < 1000, report.ToString());
            }
        }
        await drive.SettleAsync();
        Assert.Empty(drive.Errors());
    }
}
